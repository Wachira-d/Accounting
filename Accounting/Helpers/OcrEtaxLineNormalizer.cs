using System;
using System.Collections.Generic;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>เหตุที่บรรทัดที่เขียนกลับลงสแกนตอนอนุมัติ อธิบายด้วย จำนวน × ราคา − ส่วนลด ไม่ได้ (<see cref="OcrEtaxLineNormalizer.WriteBackLine"/>)</summary>
public enum OcrWriteBackLoss
{
    /// <summary>บรรทัดอธิบายได้ครบ (จำนวน × ราคา − ส่วนลด = ยอด หลังพาส่วนลดท้ายบิล)</summary>
    None = 0,
    /// <summary>เอกสารหักมัดจำ — ส่วนมัดจำไม่ตามไปเมื่อสร้างใหม่จากสแกน</summary>
    DepositDeducted = 1,
    /// <summary>ยอดมากกว่า round(จำนวน × ราคา) − ส่วนลด — ข้อมูลบรรทัดขัดกัน</summary>
    AmountAboveLine = 2,
    /// <summary>ยอดน้อยกว่า แต่เอกสารไม่มีส่วนลดท้ายบิล/มัดจำอธิบาย</summary>
    UnexplainedReduction = 3,
}

/// <summary>ราคาต่อหน่วยของบรรทัด e-Tax XML รวม VAT หรือยังไม่รวม — พิสูจน์ด้วยเลขของบรรทัดเอง (<see cref="OcrEtaxLineNormalizer.Normalize"/>)</summary>
public enum OcrEtaxPriceBasis
{
    /// <summary>พิสูจน์ไม่ได้ (ไม่มีจำนวน/ราคา/ยอด · มีค่าบริการรายบรรทัด · ตัวเลขไม่ลงตัวทั้งสองทาง/ตรงทั้งสองทาง) ⇒ จำนวน/ราคาตาม XML ·
    /// ส่วนลด = round(จำนวน × ราคา) − ยอด เมื่อไม่ติดลบ · หมายเหตุห้ามอนุมัติเองระบุเหตุ (ห้ามหารจำนวนจากยอด)</summary>
    Unknown = 0,
    /// <summary>จำนวน × ราคา − ส่วนลด ≈ <c>NetLineTotalAmount</c> (ยอดก่อน VAT) — ราคาก่อน VAT (Shopee)</summary>
    ExclusiveOfVat = 1,
    /// <summary>จำนวน × ราคา − ส่วนลด ≈ <c>NetIncludingTaxesLineTotalAmount</c> (ยอดรวม VAT) — ราคารวม VAT (CRC ไทวัสดุ)</summary>
    InclusiveOfVat = 2,
}

/// <summary>ตัวเลขของบรรทัดหนึ่งตามที่ e-Tax XML (ขมธอ.3-2560) ประกาศ — ไม่มีการคำนวณใด ๆ</summary>
/// <param name="BilledQuantity"><c>SpecifiedLineTradeDelivery/BilledQuantity</c></param>
/// <param name="GrossUnitPrice"><c>GrossPriceProductTradePrice/ChargeAmount</c> (ผู้ขายบางรายรวม VAT · บางรายไม่รวม)</param>
/// <param name="LineAllowance">Σ <c>SpecifiedTradeAllowanceCharge/ActualAmount</c> ระดับบรรทัดที่ <c>ChargeIndicator=false</c></param>
/// <param name="LineCharge">Σ ระดับบรรทัดที่ <c>ChargeIndicator=true</c> (ค่าบริการเพิ่ม) — มี ⇒ ไม่ตัดสิน</param>
/// <param name="VatRatePercent"><c>ApplicableTradeTax/CalculatedRate</c> ของบรรทัด (7 · 0)</param>
/// <param name="NetAmount"><c>NetLineTotalAmount</c> — ยอดก่อน VAT หลังส่วนลดบรรทัด</param>
/// <param name="NetIncludingVatAmount"><c>NetIncludingTaxesLineTotalAmount</c> — ยอดรวม VAT หลังส่วนลดบรรทัด</param>
public readonly record struct OcrEtaxLineFacts(
    decimal? BilledQuantity, decimal? GrossUnitPrice, decimal? LineAllowance, decimal? LineCharge,
    decimal? VatRatePercent, decimal? NetAmount, decimal? NetIncludingVatAmount);

/// <summary>บรรทัดที่เอกสารควรถือ: <c>round(จำนวน × ราคา, 2) − ส่วนลด = ยอด</c> — ทุกตัวอยู่ฐานเดียวกัน (<paramref name="PriceIncludesVat"/>)</summary>
/// <param name="Basis">ผลพิสูจน์ว่าราคาบน XML รวม/ไม่รวม VAT</param>
/// <param name="Quantity">จำนวนตาม XML — <b>ไม่เคยหารจากยอด</b></param>
/// <param name="UnitPrice">ราคาต่อหน่วย — รวม VAT ตามกระดาษเมื่อ <paramref name="PriceIncludesVat"/> · ไม่งั้นก่อน VAT (ทศนิยม 2 ตำแหน่ง · คำตัดสินรอบ 193 ข้อ 8)</param>
/// <param name="LineDiscount">ส่วนลดรายบรรทัด ฐานเดียวกับราคา (null = ไม่มี)</param>
/// <param name="Amount">ยอดหลังส่วนลด ฐานเดียวกับราคา — รวม VAT (<c>NetIncludingTaxesLineTotalAmount</c>) หรือก่อน VAT (<c>NetLineTotalAmount</c>)</param>
/// <param name="QuantityFromDocument">true = บรรทัดมาจาก XML ที่ลงนาม ⇒ ตัวกัน "จำนวนระเบิด" ห้ามเขียนทับจำนวน/ยุบ/รวมบรรทัด — <b>ทุกบรรทัด</b>ของ
/// <see cref="OcrEtaxLineNormalizer.Normalize"/> (รวมบรรทัดที่ตัดสินฐานราคาไม่ได้ · ทบทวน e8547899 ข้อ 2: ห้ามหารจำนวนจากยอดบนบรรทัด e-Tax เด็ดขาด)</param>
/// <param name="PriceIncludesVat">true = บรรทัดถือค่าตามกระดาษ "รวม VAT" ทุกตัว (เอกสารต้องเป็น <c>PricesIncludeVat</c>) — ได้จาก <see cref="OcrEtaxLineNormalizer.NormalizeInvoice"/> เท่านั้น</param>
/// <param name="VatStripResidual">ส่วนของส่วนลดที่ "แต่งขึ้น" จากการถอด VAT แล้วปัดราคา (ไม่ใช่ส่วนลดบนเอกสาร) · null = ไม่มี</param>
/// <param name="VatRate">อัตรา VAT ของบรรทัดตาม XML</param>
/// <param name="ResidualOverCap">true = เศษจากการถอด VAT เกินเพดาน แต่ยังใช้เป็นส่วนลดเพื่อให้บรรทัดลงตัว (ห้ามทิ้งบรรทัดที่ จำนวน × ราคา − ส่วนลด ≠ ยอด) —
/// ผู้เรียกต้องเขียนหมายเหตุที่ห้ามอนุมัติเอง (<see cref="OcrEtaxLineNormalizer.LineCheckTag"/>)</param>
/// <param name="UndecidedReason">เหตุที่ตัดสินฐานราคาไม่ได้ (<see cref="OcrEtaxPriceBasis.Unknown"/> เท่านั้น) — ค่าบริการรายบรรทัด · ตรงทั้งสองฐาน ·
/// ไม่ตรงทั้งสองฐาน · ข้อมูลไม่ครบ/ติดลบ — <see cref="OcrEtaxLineNormalizer.NormalizeInvoice"/> เขียนหมายเหตุห้ามอนุมัติเองตามเหตุนี้</param>
public readonly record struct OcrEtaxLine(
    OcrEtaxPriceBasis Basis, decimal? Quantity, decimal? UnitPrice, decimal? LineDiscount, decimal? Amount,
    bool QuantityFromDocument, bool PriceIncludesVat = false, decimal? VatStripResidual = null, decimal? VatRate = null,
    bool ResidualOverCap = false, string? UndecidedReason = null);

/// <summary>ผลตัดสินทั้งใบ (<see cref="OcrEtaxLineNormalizer.NormalizeInvoice"/>)</summary>
/// <param name="PricesIncludeVat">true = สร้างเอกสารแบบ "ราคารวม VAT" ด้วยค่าตามกระดาษทุกบรรทัด (กระทบยอดกับสูตรของ DocumentService แล้ว)</param>
/// <param name="Lines">บรรทัดตามลำดับ XML</param>
/// <param name="Notes">ข้อความลง ProcessingNotes (ขึ้นต้น <c>[e-Tax]</c>) — เศษจากการถอด VAT · เหตุที่ใช้ทางสำรอง</param>
public sealed record OcrEtaxInvoiceLines(bool PricesIncludeVat, IReadOnlyList<OcrEtaxLine> Lines, IReadOnlyList<string> Notes);

/// <summary>
/// <b>ตัวตัดสินตัวเดียว: บรรทัด e-Tax XML → บรรทัดเอกสาร</b> (pure · ไม่ throw) — ทางหลัก <see cref="NormalizeInvoice"/>: ใบที่ราคารวม VAT ทุกบรรทัดและ
/// กระทบยอดผ่าน ⇒ ราคารวม VAT ตามกระดาษ (<c>PricesIncludeVat</c>) · อื่น ๆ ⇒ ราคาก่อน VAT รายบรรทัด (<see cref="Normalize"/>)
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-10-09 · สแกน f1690d11 · ใบ CRC ไทวัสดุ SRCIE26100075384) ═══
/// <para>ผู้ขายรายนี้ประกาศ <c>GrossPriceProductTradePrice</c> เป็นราคา<b>รวม VAT</b> และส่วนลดบรรทัด
/// (<c>SpecifiedTradeAllowanceCharge</c>) เป็นยอด<b>รวม VAT</b> — บรรทัด 1: 3 × 37.00 − 26.68 = 84.32 (รวม VAT) ⇒ 78.80 ก่อน VAT.
/// ตัวสกัดเดิมเก็บแค่ จำนวน/ราคา/ยอด แล้วตัวกัน "จำนวนระเบิด" (<c>SanitizeVatSplitArtifacts</c>) เห็น 3 × 37 ≠ 78.80
/// จึง "แก้" จำนวน = 78.80 ÷ 37 = 2.13 · ค่าขนส่ง 120 × 1.00 กลายเป็นจำนวน 37.38 · ราคา 37 (รวม VAT) อยู่ในเอกสารราคาก่อน VAT ·
/// ส่วนลด 0% · ด่าน [Gateway] ฟ้อง 9 บรรทัด + ผลต่างปัดเศษ −0.03 ที่ไม่มีบนกระดาษ</para>
///
/// <para><b>กติกา</b> (ตัวเลขของบรรทัดเองเป็นหลักฐาน — ไม่เดาจากชื่อผู้ขาย):
/// round(จำนวน × ราคา) − ส่วนลด ≈ ยอดก่อน VAT ⇒ ราคาก่อน VAT · ≈ ยอดรวม VAT ⇒ ราคารวม VAT ⇒ ถอด VAT จากราคา
/// (round(ราคา × 100/(100+อัตรา), 2) — ขยับขึ้นเป็นราคา 2 ตำแหน่งที่น้อยที่สุดที่ round(จำนวน × ราคา) ≥ ยอดก่อน VAT) แล้วส่วนลดก่อน VAT =
/// round(จำนวน × ราคาก่อน VAT) − ยอดก่อน VAT (ลงตัวพอดีโดยการสร้าง · ไม่มีส่วนลดบนกระดาษก็อาจมี "เศษจากการถอด VAT" ไม่กี่สตางค์) ·
/// ไม่ลงตัวทั้งสองทาง / ไม่มี BilledQuantity / มีค่าบริการรายบรรทัด ⇒ <see cref="OcrEtaxPriceBasis.Unknown"/> คงค่าเดิม (จำนวน·ราคา·ยอด ตาม XML)</para>
///
/// <para><b>ส่วนลดรวมหัวใบ</b> (<c>AllowanceTotalAmount</c> 1,080.00 = Σ ส่วนลดบรรทัดรวม VAT) <b>อยู่ในยอดบรรทัดแล้ว</b> —
/// ห้ามหักซ้ำ (หัวใบใช้ LineTotal − TaxBasis = 0 ตามเดิม)</para>
/// </summary>
public static class OcrEtaxLineNormalizer
{
    /// <summary>ความคลาดของ จำนวน × ราคา กับยอดที่ XML ประกาศ — เศษปัดของราคาต่อหน่วย 1 สตางค์ (ตัวเดียวกับ <see cref="OcrTotalDecomposer.LinePrintedTol"/>)</summary>
    public const decimal Tol = OcrTotalDecomposer.LinePrintedTol;

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>เพดาน "เศษจากการถอด VAT" ที่ยอมให้เป็นส่วนลดของบรรทัดในทางสำรอง (ราคาก่อน VAT): ไม่เกิน 1.00 บาท</summary>
    public const decimal MaxVatStripResidual = 1.00m;
    /// <summary>และไม่เกิน 0.5% ของยอดบรรทัด (ก่อน VAT) — เกินนี้ = ราคาต่อหน่วย 2 ตำแหน่งบิดค่ามากเกินจะเรียกว่าเศษ ⇒ ไม่ตัดสิน (Unknown)</summary>
    public const decimal MaxVatStripResidualShare = 0.005m;

    /// <summary>ป้ายของ ProcessingNotes ที่ตัวตัดสินนี้เขียน (ข้อสังเกต — ไม่บล็อก)</summary>
    public const string NoteTag = "[e-Tax]";

    /// <summary>ป้ายห้ามอนุมัติเอง: ทั้งใบราคารวม VAT ตามกระดาษ แต่ยอดหัวใบไม่ลงตัวกับบรรทัดแม้ผ่านขั้นปัด VAT ของเอกสารแล้ว (ใบ 2614501699 รอบ 6)</summary>
    public const string HeaderGapTag = "[ETAX-HEADER-GAP]";

    /// <summary>ป้ายห้ามอนุมัติเอง: บรรทัดที่ต้องใช้เศษจากถอด VAT เกินเพดาน หรือบรรทัดที่ตัดสินฐาน VAT ไม่ได้ (จำนวน/ราคาตาม XML · ส่วนลดที่คำนวณหรือไม่ลงตัว)</summary>
    public const string LineCheckTag = "[ETAX-LINE-CHECK]";

    /// <summary>
    /// <b>ตัดสินทั้งใบ</b> — ฝ่ายค้านรอบสาม (2026-10-09): ทางราคาก่อน VAT ต้อง "แต่ง" ส่วนลดจากเศษการถอด VAT (7 × 10.00 รวม VAT ⇒ 9.35 − 0.03)
    /// ซึ่งไม่มีบนกระดาษ ⇒ ทางที่ต้องการคือ <b>ราคารวม VAT ตามกระดาษทุกตัว</b> (จำนวน · ราคา 37.00 · ส่วนลด 26.68) บนเอกสาร
    /// <c>PricesIncludeVat</c> — ใช้ได้เมื่อ <b>ทุกบรรทัด</b> พิสูจน์ราคารวม VAT และ <see cref="TiesOutInclusive"/> ยืนยันว่าสูตรของ
    /// <c>DocumentService.ComputeLineAmounts</c> (โหมดราคารวม VAT) + <c>ReconcileTaxRounding</c> ให้ยอดก่อน VAT รายบรรทัด = <c>NetLineTotalAmount</c>
    /// และยอดหัวใบ (ก่อน VAT · VAT · รวม) ตรง XML ทุกสตางค์ · ไม่ผ่านข้อใดข้อหนึ่ง ⇒ ทางสำรองราคาก่อน VAT รายบรรทัด (<see cref="Normalize"/>)
    /// </summary>
    public static OcrEtaxInvoiceLines NormalizeInvoice(
        IReadOnlyList<OcrEtaxLineFacts> lines, decimal? headerLineTotal, decimal? headerVat, decimal? grandTotal)
    {
        var notes = new List<string>();
        var proofs = lines.Select(ProveInclusive).ToList();
        if (proofs.Count > 0 && proofs.All(p => p.HasValue))
        {
            var verbatim = proofs.Select(x =>
            {
                var p = x!.Value;
                return new OcrEtaxLine(OcrEtaxPriceBasis.InclusiveOfVat, p.Qty, p.Gross, p.Allowance > 0m ? R2(p.Allowance) : null,
                    p.NetIncl, true, PriceIncludesVat: true, VatStripResidual: null, VatRate: p.Rate);
            }).ToList();
            // ใบ 2614501699 (รอบ 6): ผู้ขายคิด VAT ระดับหัวใบ (3,160 ÷ 1.07) ⇒ Σ ก่อน VAT รายบรรทัด 2,953.26 ≠ หัวใบ 2,953.27 — ขั้น
            // ReconcileTaxRounding ของเอกสารขยับบรรทัดใหญ่สุด 1 สตางค์แล้วได้หัวใบตรงกระดาษ ⇒ ตัวพิสูจน์ "เล่นซ้ำ" ขั้นนั้น (ไม่ใช่บังคับรายบรรทัดเท่า XML)
            // · ทุกบรรทัดพิสูจน์ราคารวม VAT แล้ว ⇒ ใช้ค่าตามกระดาษเสมอ (ทางนี้ถูกกว่าทางสำรองที่ต้องแต่งส่วนลด) · หัวใบยังไม่ลงตัว ⇒ หมายเหตุห้ามอนุมัติเอง
            var tieNotes = new List<string>();
            var tie = TiesOutInclusive(verbatim, proofs.Select(x => x!.Value.Net).ToList(), headerLineTotal, headerVat, grandTotal, tieNotes);
            notes.AddRange(tieNotes);
            if (tie is not null)
                notes.Add($"{HeaderGapTag} ราคาบนเอกสารรวม VAT ทุกบรรทัด — ลงตามกระดาษ แต่ถอด VAT ตามสูตรเอกสารแล้วไม่ตรงยอดใน XML ({tie}) — ตรวจยอดกับกระดาษก่อนอนุมัติ");
            return new OcrEtaxInvoiceLines(true, verbatim, notes);
        }
        var ex = new List<OcrEtaxLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var n = Normalize(lines[i]);
            if (n.ResidualOverCap)
                notes.Add($"{LineCheckTag} บรรทัดที่ {i + 1}: ราคารวม VAT ถอดเป็นราคาก่อน VAT แล้วต้องใช้เศษ {Math.Abs(n.VatStripResidual ?? 0m):0.00} บาท "
                    + $"(เกินเพดาน {MaxVatStripResidual:0.00} บาท / {MaxVatStripResidualShare * 100m:0.#}%) เป็นส่วนลด — ไม่ใช่ส่วนลดบนเอกสาร ตรวจราคา/ส่วนลดบรรทัดนี้ก่อนอนุมัติ");
            else if (n.VatStripResidual is decimal r && r != 0m)
                notes.Add($"{NoteTag} เศษจากถอด VAT {Math.Abs(r):0.00} บาท ไม่ใช่ส่วนลดบนเอกสาร — บรรทัดที่ {i + 1} (ส่วนลดก่อน VAT {(n.LineDiscount ?? 0m):0.00} รวมเศษนี้แล้ว)");
            if (n.Basis == OcrEtaxPriceBasis.Unknown)
                notes.Add(UndecidedNote(i, n));
            ex.Add(n);
        }
        return new OcrEtaxInvoiceLines(false, ex, notes);
    }

    /// <summary>บรรทัดพิสูจน์ "ราคารวม VAT" ด้วยเลขของตัวเอง: round(จำนวน × ราคา) − ส่วนลด ≈ ยอดรวม VAT (และไม่ ≈ ยอดก่อน VAT) + อัตรา &gt; 0</summary>
    private static (decimal Qty, decimal Gross, decimal Allowance, decimal Net, decimal NetIncl, decimal Rate)? ProveInclusive(OcrEtaxLineFacts f)
    {
        if (f.BilledQuantity is not decimal q || q <= 0m) return null;
        if (f.GrossUnitPrice is not decimal g || g < 0m) return null;
        if (f.NetAmount is not decimal net || net < 0m) return null;
        if (f.NetIncludingVatAmount is not decimal ni || ni == net) return null;
        if (f.LineCharge is > 0m) return null;
        var allowance = f.LineAllowance ?? 0m;
        if (allowance < 0m) return null;
        var after = R2(q * g) - allowance;
        if (Math.Abs(after - ni) > Tol || Math.Abs(after - net) <= Tol) return null;
        var rate = f.VatRatePercent is decimal vr && vr > 0m
            ? vr
            : net > 0m ? Math.Round((ni / net - 1m) * 100m, 0, MidpointRounding.AwayFromZero) : 0m;
        if (rate <= 0m) return null;
        return (q, g, allowance, net, ni, rate);
    }

    /// <summary>
    /// ตรวจว่าบรรทัด "ราคารวม VAT ตามกระดาษ" ผ่านสูตรของเอกสารแล้วได้ตัวเลขเดียวกับ XML ทุกสตางค์ — <b>สำเนาสูตรเพื่อพิสูจน์</b> ของ
    /// <c>DocumentService.ComputeLineAmounts</c> (gross = round(จำนวน × ราคา) · ส่วนลด = min(round(ส่วนลด), gross) · ถอด VAT ด้วย
    /// <see cref="DocumentLineVatConvention.SplitLine"/> ซึ่งเป็นสูตรเดียวกับสาขา PricesIncludeVat) และขั้น VAT ของ <c>ReconcileTaxRounding</c>
    /// (เป้า = round(Σ ก่อน VAT × อัตรา) ต่อกลุ่มอัตรา — ต่างเมื่อไร บรรทัดใหญ่สุดถูกขยับ ⇒ ยอดรายบรรทัดหลุดจาก XML) ·
    /// สูตรใน DocumentService ถูกล็อกด้วย <c>tools/required_call_site_check.py</c> (เปลี่ยนสูตรที่นั่น = ต้องแก้ที่นี่ด้วย) ·
    /// คืน null = ลงตัวทั้งหมด · ไม่งั้นข้อความเหตุผล
    /// </summary>
    /// <param name="lines">บรรทัดราคารวม VAT (ราคา · ส่วนลด · ยอด ตามกระดาษ · อัตรา)</param>
    /// <param name="expectedNets"><c>NetLineTotalAmount</c> ของ XML ตามลำดับบรรทัด</param>
    /// <param name="headerLineTotal"><c>LineTotalAmount</c> หัวใบ</param>
    /// <param name="headerVat"><c>TaxTotalAmount</c> หัวใบ</param>
    /// <param name="grandTotal"><c>GrandTotalAmount</c> หัวใบ</param>
    /// <param name="shiftNotes">รับหมายเหตุบรรทัดที่ขั้นปัด VAT ของเอกสารขยับ (ก่อน VAT ต่างจาก XML เท่าที่ขยับ) · null = ไม่เก็บ</param>
    internal static string? TiesOutInclusive(IReadOnlyList<OcrEtaxLine> lines, IReadOnlyList<decimal> expectedNets,
        decimal? headerLineTotal, decimal? headerVat, decimal? grandTotal, List<string>? shiftNotes = null)
    {
        if (headerLineTotal is not decimal hl || headerVat is not decimal hv || grandTotal is not decimal gt)
            return "หัวใบไม่มียอดก่อน VAT/VAT/ยอดรวม ให้เทียบ";
        if (lines.Count == 0 || lines.Count != expectedNets.Count) return "ไม่มีบรรทัด";
        var nets = new decimal[lines.Count];
        var vats = new decimal[lines.Count];
        var rates = new decimal[lines.Count];
        var grossSum = 0m;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Quantity is not decimal q || l.UnitPrice is not decimal up || l.Amount is not decimal amt
                || l.VatRate is not decimal rate || rate <= 0m)
                return $"บรรทัดที่ {i + 1} ข้อมูลไม่ครบ";
            var lineGross = R2(q * up);
            var disc = l.LineDiscount is decimal d && d > 0m ? Math.Min(R2(d), lineGross) : 0m;
            var after = lineGross - disc;
            if (after != amt) return $"บรรทัดที่ {i + 1}: {q:0.##} × {up:0.00} − {disc:0.00} = {after:0.00} ≠ ยอดรวม VAT {amt:0.00}";
            var split = DocumentLineVatConvention.SplitLine(after, rate, null, includeVat: true);
            nets[i] = split.Net;
            vats[i] = split.Vat;
            rates[i] = rate;
            if (nets[i] != expectedNets[i]) return $"บรรทัดที่ {i + 1}: ถอด VAT ได้ {nets[i]:0.00} ≠ ยอดก่อน VAT ใน XML {expectedNets[i]:0.00}";
            grossSum += after;
        }
        // เล่นซ้ำขั้น VAT ของ ReconcileTaxRounding (โหมดราคารวม VAT) — บรรทัดที่ถูกขยับ ก่อน VAT ต่างจาก XML ได้เท่าที่ขยับ (บันทึกหมายเหตุ)
        foreach (var (idx, shift) in ReplayTaxRounding(nets, vats, rates))
            shiftNotes?.Add($"{NoteTag} บรรทัดที่ {idx + 1}: ยอดก่อน VAT ในเอกสาร {nets[idx]:0.00} (XML {nets[idx] + shift:0.00}) — ขั้นปัด VAT ระดับเอกสาร "
                + $"ขยับ {(-shift):+0.00;-0.00} เพื่อให้ VAT รวมตรงหัวใบ (ผู้ขายคิด VAT จากยอดรวมทั้งใบ)");
        if (nets.Sum() != hl) return $"Σ ก่อน VAT {nets.Sum():0.00} ≠ หัวใบ {hl:0.00}";
        if (vats.Sum() != hv) return $"Σ VAT {vats.Sum():0.00} ≠ หัวใบ {hv:0.00}";
        if (grossSum != gt) return $"Σ รวม VAT {grossSum:0.00} ≠ ยอดรวมหัวใบ {gt:0.00}";
        return null;
    }

    /// <summary>
    /// <b>สำเนาเพื่อพิสูจน์</b>ของขั้น VAT ใน <c>DocumentService.ReconcileTaxRounding</c> (โหมดราคารวม VAT): ต่อกลุ่มอัตรา (เฉพาะบรรทัดอัตรา &gt; 0 และยอดก่อน VAT ≠ 0)
    /// เป้า = round(Σ ก่อน VAT × อัตรา/100) · ต่าง = เป้า − Σ VAT · ต่างไม่เป็น 0 และ |ต่าง| ≤ 1 ⇒ บรรทัดที่ |ก่อน VAT| มากที่สุด (ตัวแรกเมื่อเท่ากัน)
    /// VAT += ต่าง · ก่อน VAT −= ต่าง · แก้ <paramref name="nets"/>/<paramref name="vats"/> ในที่ · คืน (บรรทัด, ต่าง) ที่ขยับ ·
    /// สูตรต้นทางล็อกด้วย <c>tools/required_call_site_check.py</c></summary>
    /// <param name="pricesIncludeVat">โหมดของเอกสาร — ราคารวม VAT: ก่อน VAT ขยับสวน VAT (ยอดรวม VAT ของบรรทัดคงที่) · ราคาก่อน VAT: ขยับเฉพาะ VAT</param>
    internal static List<(int Index, decimal Shift)> ReplayTaxRounding(decimal[] nets, decimal[] vats, decimal[] rates, bool pricesIncludeVat = true)
    {
        var shifted = new List<(int Index, decimal Shift)>();
        foreach (var g in Enumerable.Range(0, nets.Length)
                     .Where(i => rates[i] > 0m && nets[i] != 0m)
                     .GroupBy(i => rates[i]))
        {
            var target = R2(g.Sum(i => nets[i]) * g.Key / 100m);
            var diff = target - g.Sum(i => vats[i]);
            if (diff == 0m || Math.Abs(diff) > 1m) continue;
            var j = g.OrderByDescending(i => Math.Abs(nets[i])).First();
            vats[j] += diff;
            if (pricesIncludeVat) nets[j] -= diff;
            shifted.Add((j, diff));
        }
        return shifted;
    }

    /// <summary>VAT รายบรรทัดของเอกสาร "ราคารวม VAT ตามกระดาษ" = สูตรเดียวกับ ComputeLineAmounts (<see cref="DocumentLineVatConvention.SplitLine"/>) —
    /// ตัวสร้างบรรทัดจากสแกนใช้แทนการเฉลี่ย VAT หัวใบตามสัดส่วน (ที่ต่างรายบรรทัดได้ 1 สตางค์ ⇒ ยอดก่อน VAT รายบรรทัดไม่ตรง XML)
    /// · คืน null เมื่อมีบรรทัดที่ไม่ใช่ราคารวม VAT ตามกระดาษ หรือ Σ ≠ VAT หัวใบ (ผู้เรียกใช้การเฉลี่ยเดิม)</summary>
    public static decimal[]? InclusiveLineVats(IReadOnlyList<(decimal Amount, decimal VatRate, bool PriceIncludesVat)> lines, decimal headerVat)
    {
        // บรรทัดยอด 0 ที่ไม่ติดธง (แถวว่างที่ผู้ใช้เพิ่งเพิ่ม) ไม่มี VAT ให้ถอด — ไม่ทำให้ทั้งใบตกทางเฉลี่ย (ฝ่ายค้านรอบสี่ ข้อ 2)
        if (!AllLinesPriceIncludeVat(lines.Select(l => (l.Amount, l.PriceIncludesVat)).ToList())) return null;
        if (lines.Any(l => l.PriceIncludesVat && l.VatRate <= 0m)) return null;
        var nets = new decimal[lines.Count];
        var vats = new decimal[lines.Count];
        var rates = new decimal[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].PriceIncludesVat) continue;   // แถวว่าง ยอด 0
            (nets[i], vats[i]) = DocumentLineVatConvention.SplitLine(lines[i].Amount, lines[i].VatRate, null, includeVat: true);
            rates[i] = lines[i].VatRate;
        }
        // ผลเดียวกับที่ DocumentService จะได้ตอนบันทึก (ComputeLineAmounts + ReconcileTaxRounding) — ใบ 2614501699: บรรทัดใหญ่สุด VAT −0.01
        ReplayTaxRounding(nets, vats, rates);
        return vats.Sum() == headerVat ? vats : null;
    }

    /// <summary>อัตรา VAT ของบรรทัดเอกสารจากบรรทัด e-Tax (<c>MapEtaxToOcrData</c> · รอบ 7 เมทริกซ์รูปแบบ XML): บรรทัดที่พิสูจน์ราคารวม VAT ⇒ อัตราที่ใช้ถอด ·
    /// อื่น ๆ ⇒ <c>CalculatedRate</c> ของบรรทัดใน XML ที่ลงนาม (&gt; 0 ⇒ ตามนั้น · 0 ⇒ ไม่มี VAT: "ยกเว้น" เมื่อชื่อเข้าหมวดยกเว้น §81 ไม่งั้น 0%) ·
    /// ไม่มี/ติดลบ ⇒ null (ชั้นถัดไปของตัวสร้างบรรทัดตัดสิน)
    /// <para>ที่มา: เดิมส่งอัตราเฉพาะทางราคารวม VAT ⇒ บรรทัดราคาก่อน VAT ทุกบรรทัดถูกเดาอัตราจากชื่อ (7%) ⇒ ใบผสม 7%/0% ที่บรรทัด 0% ยอดเล็ก
    /// (VAT ส่วนต่าง &lt; 0.10 ต่ำกว่าเกณฑ์ด่านอัตรา) ได้บรรทัด 0% ติด 7% + VAT ที่เฉลี่ยจากหัวใบ <b>เงียบ ๆ</b> · ยอดใหญ่ตก [Σ-GAP] ทั้งที่ XML บอกอัตราไว้แล้ว</para></summary>
    public static decimal? LineVatRate(OcrEtaxLine line, decimal? xmlRate, string? description)
    {
        if (line.VatRate is decimal r && r > 0m) return r;   // อัตราที่ตัวตัดสินใช้ถอด VAT (รวมอัตราที่อนุมานจากยอดสองตัวของบรรทัด)
        if (xmlRate is not decimal x || x < 0m) return null;
        if (x > 0m) return x;
        return ThaiVatTypeRule.LooksExempt(description) ? ThaiVatTypeRule.ExemptRate : 0m;
    }

    /// <summary>ทั้งใบเป็น "ราคารวม VAT ตามกระดาษ" ไหม — มีบรรทัดที่ติดธงอย่างน้อยหนึ่ง และทุกบรรทัดที่ยอดไม่เป็น 0 ติดธง ·
    /// บรรทัดยอด 0 ที่ไม่ติดธง (แถวว่างที่เพิ่งเพิ่ม) ไม่นับ — ฝ่ายค้านรอบสี่ ข้อ 2: เดิม <c>items.All(...)</c> ⇒ เพิ่มแถวเดียว
    /// ทั้งใบหลุดจากราคารวม VAT แล้ว 9 บรรทัดรวม VAT ถูกตีเป็นยอดก่อน VAT (VAT 209.41 → 227.57)</summary>
    public static bool AllLinesPriceIncludeVat(IReadOnlyList<(decimal Amount, bool PriceIncludesVat)> lines)
        => lines.Any(l => l.PriceIncludesVat) && lines.All(l => l.PriceIncludesVat || l.Amount == 0m);

    /// <summary>ค่าที่แถวใหม่ (ปุ่ม "เพิ่มบรรทัด" ในหน้ารีวิว) ต้องสืบทอด: ถ้าทุกบรรทัดที่ยอดไม่เป็น 0 ถือราคารวม VAT ตามกระดาษ ⇒ แถวใหม่ก็รวม VAT
    /// (ผู้ใช้กรอกตัวเลขตามกระดาษซึ่งรวม VAT) + อัตราเดียวกันเมื่อทุกบรรทัดอัตราเดียว (ต่างกัน = null ให้ตัวสร้างบรรทัดตัดสิน) ·
    /// ไม่ใช่ใบราคารวม VAT = (false, null) พฤติกรรมเดิม</summary>
    public static (bool PriceIncludesVat, decimal? VatRate) InheritForNewLine(IReadOnlyList<(decimal Amount, bool PriceIncludesVat, decimal? VatRate)> existing)
    {
        if (!AllLinesPriceIncludeVat(existing.Select(l => (l.Amount, l.PriceIncludesVat)).ToList())) return (false, null);
        var rates = existing.Where(l => l.PriceIncludesVat).Select(l => l.VatRate).Distinct().ToList();
        return (true, rates.Count == 1 ? rates[0] : null);
    }

    /// <summary>ยอด<b>ก่อน VAT</b> ของบรรทัดสแกน — ฐานต้นทุนสินทรัพย์ถาวร/สต็อก (ฝ่ายค้านรอบสี่ ข้อ 1: บรรทัดราคารวม VAT ตามกระดาษถือยอดรวม VAT
    /// 524.17 ⇒ เดิมสินทรัพย์ + ขาเดบิต JE เกินจริง 7% แทน 489.88) · ไม่ใช่ราคารวม VAT = ยอดเดิม
    /// <para>ฝ่ายค้านรอบห้า ข้อ 4: บรรทัดราคารวม VAT ที่ไม่รู้อัตรา (แถวที่เพิ่มในใบหลายอัตรา) ⇒ ใช้ <paramref name="fallbackRate"/> (อัตราหลักของใบ ·
    /// <see cref="DominantVatRate"/>) · ไม่มีอีก ⇒ <b>null</b> (ผู้เรียกต้องใช้ทางที่ปลอดภัย + บอกผู้ใช้) — เดิมคืนยอดรวม VAT ตรง ๆ = ตีเป็นก่อน VAT เงียบ ๆ</para></summary>
    public static decimal? ExVatAmount(decimal? amount, bool priceIncludesVat, decimal? vatRate, decimal? fallbackRate = null)
    {
        if (!priceIncludesVat) return amount;
        var rate = vatRate is > 0m ? vatRate : fallbackRate is > 0m ? fallbackRate : null;
        if (rate is not decimal vr || amount is not decimal a) return null;
        return DocumentLineVatConvention.SplitLine(a, vr, null, includeVat: true).Net;
    }

    /// <summary>อัตรา VAT หลักของใบ "ราคารวม VAT ตามกระดาษ" = อัตราที่บรรทัดติดธงใช้มากที่สุด (เสมอกัน/ไม่มี = null) — ใช้แทนอัตราของแถวที่ไม่รู้อัตรา</summary>
    public static decimal? DominantVatRate(IEnumerable<(bool PriceIncludesVat, decimal? VatRate)> lines)
    {
        var groups = lines.Where(l => l.PriceIncludesVat && l.VatRate is > 0m)
            .GroupBy(l => l.VatRate!.Value)
            .Select(g => (Rate: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ToList();
        if (groups.Count == 0 || (groups.Count > 1 && groups[0].Count == groups[1].Count)) return null;
        return groups[0].Rate;
    }

    /// <summary>แปลงราคาที่ผู้ใช้กรอก "ก่อน VAT" ในแถวของใบราคารวม VAT ให้เป็นฐานรวม VAT ของใบ (ฝ่ายค้านรอบห้า ข้อ 3 — ใบเอกสารมีธงราคารวม VAT
    /// ระดับเอกสารเดียว บรรทัดปนฐานไม่ได้ ⇒ แปลงแถวนั้นให้อยู่ฐานเดียวกัน แทนการปล่อยให้ทั้งใบหลุดเป็นก่อน VAT) · round(ค่า × (100+อัตรา)/100, 2) ·
    /// ไม่รู้อัตรา ⇒ null (ไม่แปลง — ผู้เรียกบอกผู้ใช้)</summary>
    internal static (decimal UnitPrice, decimal? Discount)? ConvertExVatEntryToInclusive(decimal unitPrice, decimal? discount, decimal? vatRate)
    {
        if (vatRate is not decimal vr || vr <= 0m) return null;
        var factor = (100m + vr) / 100m;
        decimal? d = discount is > 0m ? R2(discount.Value * factor) : null;
        return (R2(unitPrice * factor), d);
    }

    /// <summary>ผลแก้ราคา/ส่วนลดของแถวในตาราง review (<see cref="ApplyReviewPriceEntry"/>)</summary>
    /// <param name="UnitPrice">ราคาที่ต้องเก็บ (ฐานของแถว)</param>
    /// <param name="Discount">ส่วนลดที่ต้องเก็บ (null = ไม่มี)</param>
    /// <param name="DiscountChanged">true = ส่วนลดเปลี่ยนจากค่าที่เก็บไว้ (ค่าใหม่เป็นของผู้ใช้ ⇒ ล้างเศษจากถอด VAT)</param>
    /// <param name="AppliedRate">อัตราที่ใช้แปลงราคาไม่รวม VAT (null = ไม่ได้แปลง)</param>
    /// <param name="ConversionNote">ข้อความถึงผู้ใช้ (แปลงแล้ว / ไม่แปลงเพราะราคาไม่ได้ถูกแก้) · null = ไม่มีอะไรต้องบอก</param>
    public readonly record struct OcrReviewPriceEdit(decimal? UnitPrice, decimal? Discount, bool DiscountChanged, decimal? AppliedRate,
        string? ConversionNote);

    /// <summary>แก้ราคา/ส่วนลดของแถวในตาราง review (ตัวตัดสินเดียวของ <c>OcrService.SetExtractedLineFieldsAsync</c>) — ทบทวน e8547899 ข้อ 1 (MEDIUM):
    /// เดิมช่องติ๊ก "กรอกราคาไม่รวม VAT" บันทึกเองทุกครั้งที่ติ๊ก แล้วเซิร์ฟเวอร์แปลง "ราคาที่เก็บไว้" ⇒ พิมพ์ราคาก่อนแล้วค่อยติ๊ก = แปลงราคาที่แปลงแล้ว
    /// · ติ๊กสองรอบ = คิด VAT ซ้ำสองชั้น · ส่วนลดรวม VAT ที่ไม่ได้แตะก็ถูกคูณ 1.07 อีก ⇒ กติกา (ทุกคำขอ idempotent):
    /// <list type="bullet">
    /// <item>แปลงเฉพาะ <b>ราคาที่ส่งมาในคำขอนี้</b> — ธงมาโดยไม่มีราคา ⇒ ปฏิเสธ (ห้ามแปลงราคาที่เก็บไว้) · ผลคิดจากค่าที่ส่งมาไม่ใช่ค่าที่เก็บ ⇒
    /// คำขอเดิมซ้ำสองครั้งได้สถานะเดียวกัน</item>
    /// <item>แปลงส่วนลดเฉพาะเมื่อค่าที่ส่งมาต่างจากค่าที่เก็บไว้ (ส่วนลดที่ไม่ได้แตะเป็นฐานรวม VAT อยู่แล้ว)</item>
    /// <item>หน้าส่งธงเฉพาะคำขอที่ช่องราคาถูกพิมพ์ใหม่หลังติ๊ก (ช่องติ๊กไม่บันทึกเอง — <c>document-scan.html</c> <c>exVatHint</c>)</item>
    /// </list></summary>
    public static OcrReviewPriceEdit ApplyReviewPriceEntry(decimal? storedUnitPrice, decimal? storedDiscount, bool rowPriceIncludesVat,
        decimal? rowVatRate, decimal? fallbackVatRate, decimal? sentUnitPrice, decimal? sentDiscount, bool priceEnteredExVat)
    {
        var price = sentUnitPrice ?? storedUnitPrice;
        var disc = storedDiscount;
        var discChanged = false;
        if (sentDiscount.HasValue)
        {
            // ปัด 2 ตำแหน่งตอนเก็บเสมอ (ฝ่ายค้านรอบสาม ข้อ 4)
            var sd = sentDiscount.Value > 0m ? R2(sentDiscount.Value) : 0m;
            if (sd != (storedDiscount ?? 0m))
            {
                disc = sd > 0m ? sd : null;
                discChanged = true;
            }
        }
        if (!priceEnteredExVat) return new OcrReviewPriceEdit(price, disc, discChanged, null, null);

        if (sentUnitPrice is not decimal entered)
            throw new BusinessRuleException("ติ๊ก \"กรอกราคาไม่รวม VAT\" ต้องมาพร้อมราคาที่กรอกใหม่ในคำขอเดียวกัน — ระบบไม่แปลงราคาที่บันทึกไว้แล้ว (กันคิด VAT ซ้ำ)");
        if (!rowPriceIncludesVat)
            throw new BusinessRuleException("แถวนี้เป็นราคาก่อน VAT อยู่แล้ว — ไม่ต้องแปลง");
        var rate = rowVatRate is > 0m ? rowVatRate : fallbackVatRate;
        var converted = ConvertExVatEntryToInclusive(entered, discChanged ? disc : null, rate)
            ?? throw new BusinessRuleException(
                "ไม่รู้อัตรา VAT ของแถวนี้ — แปลงราคาไม่รวม VAT เป็นราคารวม VAT ไม่ได้ กรอกราคารวม VAT เอง หรือแก้ที่ฟอร์มเอกสาร");
        var note = $"แปลงราคาไม่รวม VAT {entered:N2} เป็นราคารวม VAT {converted.UnitPrice:N2} (VAT {rate:0.##}%) ให้อยู่ฐานเดียวกับทั้งใบ";
        if (discChanged && disc.HasValue)
            note += $" · ส่วนลด {disc.Value:N2} เป็น {(converted.Discount ?? 0m):N2}";
        return new OcrReviewPriceEdit(converted.UnitPrice, discChanged ? converted.Discount : disc, discChanged, rate, note);
    }

    /// <summary>ป้ายฐานราคาของแถวในตาราง review (เซิร์ฟเวอร์เขียน — ฝ่ายค้านรอบห้า ข้อ 3): แถวราคารวม VAT ตามกระดาษ ⇒ ข้อความของช่องติ๊ก
    /// "กรอกราคาไม่รวม VAT" (ระบบแปลงด้วย <see cref="ConvertExVatEntryToInclusive"/>) · แถวอื่น = null (ไม่แสดงช่อง)</summary>
    public static string? PriceBasisLabel(bool priceIncludesVat)
        => priceIncludesVat ? "ราคารวม VAT · ติ๊กถ้ากรอกราคาไม่รวม VAT" : null;

    /// <summary>หมายเหตุ <c>[ASSET-COST]</c> ของผู้สมัครสินทรัพย์ถาวรตอนสแกน (ฝ่ายค้านรอบห้า ข้อ 2) — กระดาษที่ไม่ใช่ e-Tax: ระบบยังไม่รู้ตอนนั้นว่า
    /// ทั้งใบพิมพ์ราคารวม VAT ไหม (ตัดสินตอนสร้างบรรทัดเอกสาร) ⇒ ไม่เดา บอกให้ตรวจ · บรรทัดราคารวม VAT ที่ไม่รู้อัตรา ⇒ ไม่เสนอราคา · null = ไม่ต้องเตือน</summary>
    public static string? AssetCostNote(bool paperScanWithVat, IReadOnlyList<int> inclusiveUnknownRateLines)
    {
        var parts = new List<string>();
        if (paperScanWithVat)
            parts.Add("ราคาซื้อสินทรัพย์ที่เสนอมาจากยอดบรรทัดบนกระดาษ — ถ้ากระดาษพิมพ์ราคารวม VAT ต้นทุนสินทรัพย์ต้องเป็นยอดก่อน VAT (ตรวจก่อนลงทะเบียน)");
        if (inclusiveUnknownRateLines.Count > 0)
            parts.Add($"บรรทัดที่ {string.Join(", ", inclusiveUnknownRateLines)} ราคารวม VAT แต่ไม่รู้อัตรา VAT — ไม่เสนอราคาซื้อ กรอกเองตอนลงทะเบียน");
        return parts.Count == 0 ? null : AssetCostTag + " " + string.Join(" · ", parts);
    }

    /// <summary>ป้ายหมายเหตุต้นทุนสินทรัพย์จากสแกน (ไม่บล็อก — คำเตือนบนหน้า)</summary>
    public const string AssetCostTag = "[ASSET-COST]";

    /// <summary>ป้ายหมายเหตุบนสแกนเมื่อเขียนบรรทัดที่อนุมัติกลับแล้วอธิบายการหักมัดจำ/ข้อมูลขัดกันไม่ได้ — <c>OcrPostingReadiness</c> ห้ามอนุมัติเอง</summary>
    public const string WriteBackLostTag = "[SCAN-WRITEBACK]";

    /// <summary><see cref="ExVatAmount"/> ของบรรทัดที่ <paramref name="lineIndex"/> ใน <c>ExtractedItemsJson</c> — คืนค่าเฉพาะบรรทัดที่ถือราคารวม VAT
    /// ตามกระดาษ (null = ให้ผู้เรียกใช้ค่าเดิมของตัวเอง) · JSON เสีย/ไม่มีบรรทัด = null (ไม่ throw)</summary>
    public static decimal? ExVatAmountOfItem(string? itemsJson, int lineIndex)
    {
        if (string.IsNullOrWhiteSpace(itemsJson) || lineIndex < 0) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(itemsJson);
            var arr = doc.RootElement;
            if (arr.ValueKind != System.Text.Json.JsonValueKind.Array || lineIndex >= arr.GetArrayLength()) return null;
            var el = arr[lineIndex];
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object
                || !el.TryGetProperty("PriceIncludesVat", out var f) || f.ValueKind != System.Text.Json.JsonValueKind.True)
                return null;
            decimal? amount = el.TryGetProperty("Amount", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number ? a.GetDecimal() : null;
            decimal? rate = el.TryGetProperty("VatRate", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Number ? r.GetDecimal() : null;
            // ไม่รู้อัตราของบรรทัด ⇒ อัตราหลักของใบ · ยังไม่รู้ ⇒ 0 (ไม่ใช่ null): ผู้เรียกต้องไม่ตกไปใช้ยอดรวม VAT — ด่าน "ราคาซื้อต้องมากกว่า 0"
            // ของผู้เรียกบังคับให้กรอกราคาเอง (หมายเหตุ [ASSET-COST] บนสแกนบอกเหตุผลไว้แล้ว)
            var fallback = DominantVatRate(arr.EnumerateArray()
                .Where(x => x.ValueKind == System.Text.Json.JsonValueKind.Object)
                .Select(x => (
                    x.TryGetProperty("PriceIncludesVat", out var xf) && xf.ValueKind == System.Text.Json.JsonValueKind.True,
                    x.TryGetProperty("VatRate", out var xr) && xr.ValueKind == System.Text.Json.JsonValueKind.Number ? xr.GetDecimal() : (decimal?)null))
                .ToList());
            return ExVatAmount(amount, true, rate, fallback) ?? 0m;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>บรรทัดที่เขียนกลับลง <c>ExtractedItemsJson</c> ตอนอนุมัติ (ฝ่ายค้านรอบสี่ ข้อ 3) — ยอดฐานเดียวกับราคา (เอกสารราคารวม VAT ⇒
    /// ก่อน VAT + VAT ของบรรทัด) และส่วนลดที่ทำให้ <c>round(จำนวน × ราคา) − ส่วนลด = ยอด</c> จริง:
    /// <list type="bullet">
    /// <item>ส่วนลดบรรทัดอธิบายยอดได้อยู่แล้ว ⇒ ส่วนลดบรรทัด (พฤติกรรมเดิม)</item>
    /// <item>เอกสารมีส่วนลดท้ายบิล (ไม่มีหักมัดจำ) ⇒ ส่วนลด = ส่วนลดบรรทัด + ส่วนแบ่งส่วนลดท้ายบิลของบรรทัด (round(จำนวน × ราคา) − ยอด)
    ///   — สร้างใหม่จากสแกนได้ยอดเดิมทุกสตางค์</item>
    /// <item>อื่น ๆ (หักมัดจำ · ข้อมูลขัดกัน) ⇒ คงส่วนลดบรรทัด + <c>Lost = true</c> ให้ผู้เรียกเขียนหมายเหตุ (ห้ามทิ้งเงียบ)</item>
    /// </list></summary>
    /// <param name="billDiscount"><c>Document.BillDiscountAmount</c> (ส่วนลดการค้าท้ายบิล)</param>
    /// <param name="depositBase"><c>Document.DepositBaseDeducted</c> (ฐานหักมัดจำ) — ฝ่ายค้านรอบห้า ข้อ 5: มีมัดจำด้วย ⇒ ส่วนแบ่งส่วนลดการค้ายังไปกับส่วนลดบรรทัด
    /// (แบ่งส่วนที่บรรทัดถูกหักตามสัดส่วน ส่วนลดการค้า : มัดจำ) · เฉพาะส่วนมัดจำที่ "หาย" ⇒ ยอดที่เขียน = ก่อนหักมัดจำ + <c>Lost = true</c></param>
    /// <returns><c>Loss</c> = เหตุที่บรรทัดอธิบายด้วย จำนวน × ราคา − ส่วนลด ไม่ได้ (ทบทวน e8547899 ข้อ 3: หมายเหตุต้องบอกเหตุจริง — เดิมเขียน
    /// "ยอดหลังหักมัดจำ" ทุกกรณี ทั้งที่บรรทัดที่หายอาจไม่มีมัดจำเลย) · <c>Lost</c> = <c>Loss != None</c></returns>
    public static (decimal Amount, decimal? Discount, bool Lost, OcrWriteBackLoss Loss) WriteBackLine(decimal quantity, decimal unitPrice,
        decimal lineAmount, decimal lineVat, decimal lineDiscount, bool pricesIncludeVat, decimal billDiscount, decimal depositBase)
    {
        var amount = pricesIncludeVat ? lineAmount + lineVat : lineAmount;
        var gross = R2(quantity * unitPrice);
        var lineDisc = lineDiscount > 0m ? Math.Min(R2(lineDiscount), gross) : 0m;
        decimal? kept = lineDisc > 0m ? lineDisc : null;
        if (gross - lineDisc == amount) return (amount, kept, false, OcrWriteBackLoss.None);
        var reduction = gross - lineDisc - amount;
        if (reduction < 0m) return (amount, kept, true, OcrWriteBackLoss.AmountAboveLine);
        var headerReduction = Math.Max(0m, billDiscount) + Math.Max(0m, depositBase);
        if (headerReduction <= 0m) return (amount, kept, true, OcrWriteBackLoss.UnexplainedReduction);
        if (depositBase <= 0m) return (amount, lineDisc + reduction, false, OcrWriteBackLoss.None);
        var tradeShare = billDiscount > 0m ? R2(reduction * billDiscount / headerReduction) : 0m;
        var disc = lineDisc + tradeShare;
        return (gross - disc, disc > 0m ? disc : null, true, OcrWriteBackLoss.DepositDeducted);
    }

    /// <summary>หมายเหตุ <see cref="WriteBackLostTag"/> ของเอกสารหนึ่งใบ — แยกเหตุรายบรรทัด (หักมัดจำ · ยอดมากกว่าบรรทัด · ยอดถูกลดโดยไม่มีอะไรอธิบาย) ·
    /// ไม่มีบรรทัดที่หาย ⇒ null</summary>
    public static string? WriteBackLostNote(string? documentNumber, IReadOnlyList<(int Line, OcrWriteBackLoss Loss)> lost)
    {
        var parts = lost.Where(x => x.Loss != OcrWriteBackLoss.None)
            .GroupBy(x => x.Loss)
            .OrderBy(g => (int)g.Key)
            .Select(g => $"บรรทัดที่ {string.Join(", ", g.Select(x => x.Line))} {WriteBackLossText(g.Key)}")
            .ToList();
        if (parts.Count == 0) return null;
        return $"{WriteBackLostTag} เอกสาร {documentNumber}: {string.Join(" · ", parts)} — อธิบายด้วย จำนวน × ราคา − ส่วนลด ไม่ได้ "
            + "ถ้าสร้างเอกสารใหม่จากสแกนนี้ ส่วนนั้นจะไม่ตามไป ตรวจกับเอกสารเดิมก่อนอนุมัติ";
    }

    private static string WriteBackLossText(OcrWriteBackLoss loss) => loss switch
    {
        OcrWriteBackLoss.DepositDeducted => "ยอดหลังหักมัดจำ (เขียนกลับเป็นยอดก่อนหักมัดจำ)",
        OcrWriteBackLoss.AmountAboveLine => "ยอดมากกว่า จำนวน × ราคา − ส่วนลด (ข้อมูลบรรทัดขัดกัน)",
        _ => "ยอดถูกลดโดยไม่มีส่วนลดท้ายบิล/มัดจำอธิบาย",
    };


    /// <summary>ป้ายของช่องส่วนลดในตาราง review (เซิร์ฟเวอร์เป็นคนเขียน — หน้าแสดงอย่างเดียว) · null = ไม่มีส่วนลด</summary>
    public static string? DiscountLabel(decimal? lineDiscount, bool priceIncludesVat, decimal? vatStripResidual)
    {
        if (lineDiscount is not > 0m) return null;
        if (priceIncludesVat) return "ส่วนลดของบรรทัด (รวม VAT — ราคา/ส่วนลด/รวมของบรรทัดนี้รวม VAT ตามกระดาษ)";
        if (vatStripResidual is decimal r && r != 0m)
            return $"ส่วนลดก่อน VAT — รวมเศษจากการถอด VAT {Math.Abs(r):0.00} บาท (ไม่ใช่ส่วนลดบนเอกสาร)";
        return "ส่วนลดของบรรทัด (ก่อน VAT)";
    }

    /// <summary>เหตุที่ตัดสินฐานราคาไม่ได้ — ข้อความในหมายเหตุ <see cref="LineCheckTag"/> (ทบทวน e8547899 ข้อ 2: บอกเหตุจริง ไม่ใช่ข้อความเดียวทุกกรณี)</summary>
    internal const string ReasonMissing = "XML ไม่มีจำนวน/ราคา/ยอดของบรรทัด";
    internal const string ReasonNegative = "ตัวเลขของบรรทัดติดลบ (จำนวน/ราคา/ยอด/ส่วนลด)";
    internal const string ReasonCharge = "บรรทัดมีค่าบริการเพิ่ม (ChargeIndicator=true) ซึ่งเอกสารไม่มีช่องรองรับ";
    internal const string ReasonBoth = "จำนวน × ราคา − ส่วนลด ตรงทั้งยอดก่อน VAT และยอดรวม VAT (VAT ของบรรทัดน้อยจนแยกฐานไม่ได้)";
    internal const string ReasonNeither = "จำนวน × ราคา − ส่วนลด ไม่ตรงทั้งยอดก่อน VAT และยอดรวม VAT";
    internal const string ReasonNoRate = "ราคารวม VAT แต่หาอัตรา VAT ของบรรทัดไม่ได้";

    /// <summary>บรรทัดที่ตัดสินฐานราคาไม่ได้ — <b>จำนวนและราคาตาม XML เสมอ</b> (ห้ามหารจำนวนจากยอด · ทบทวน e8547899 ข้อ 2: เดิมปล่อยให้ตัวแก้จำนวน
    /// แบบเดิมทำ 3 → 2.13 ซึ่งเป็นจำนวนที่แต่งขึ้นบนบรรทัดของเอกสารที่ลงนาม) · ส่วนลด = round(จำนวน × ราคา) − ยอด เมื่อไม่ติดลบ (บรรทัดลงตัวตาม
    /// สูตร ComputeLineAmounts) · ติดลบ/ข้อมูลไม่ครบ ⇒ คงค่าตาม XML ไม่ลงตัว · ทั้งสองกรณี <see cref="NormalizeInvoice"/> เขียน <see cref="LineCheckTag"/></summary>
    private static OcrEtaxLine Undecided(OcrEtaxLineFacts f, string reason)
    {
        decimal? disc = null;
        if (f.BilledQuantity is decimal q && q > 0m && f.GrossUnitPrice is decimal g && g >= 0m && f.NetAmount is decimal net && net >= 0m)
        {
            var d = R2(q * g) - net;
            if (d > 0m) disc = d;
        }
        return new OcrEtaxLine(OcrEtaxPriceBasis.Unknown, f.BilledQuantity, f.GrossUnitPrice, disc, f.NetAmount, true, UndecidedReason: reason);
    }

    /// <summary>บรรทัดลงตัวตามสูตรเอกสาร: จำนวน &gt; 0 · ราคา ≥ 0 · 0 ≤ ส่วนลด ≤ round(จำนวน × ราคา) · round(จำนวน × ราคา) − ส่วนลด = ยอด</summary>
    private static bool LineTiesOut(OcrEtaxLine n)
        => n.Quantity is decimal q && q > 0m && n.UnitPrice is decimal p && p >= 0m && n.Amount is decimal a && a >= 0m
           && R2(q * p) - (n.LineDiscount ?? 0m) == a;

    /// <summary>หมายเหตุห้ามอนุมัติเองของบรรทัดที่ตัดสินไม่ได้ — ระบุเหตุจริง + บอกว่าบรรทัดลงตัวด้วยส่วนลดที่คำนวณ หรือยังไม่ลงตัว</summary>
    private static string UndecidedNote(int index, OcrEtaxLine n)
    {
        var reason = n.UndecidedReason ?? ReasonNeither;
        return LineTiesOut(n)
            ? $"{LineCheckTag} บรรทัดที่ {index + 1}: ตัดสินไม่ได้ว่าราคารวมหรือไม่รวม VAT — {reason} · ลงจำนวน/ราคาตาม XML "
              + $"ส่วนลด {(n.LineDiscount ?? 0m):0.00} ให้ยอด {n.Amount:0.00} ตรง XML (ไม่หารจำนวนจากยอด) — ตรวจกับกระดาษก่อนอนุมัติ"
            : $"{LineCheckTag} บรรทัดที่ {index + 1}: ตัดสินไม่ได้ว่าราคารวมหรือไม่รวม VAT — {reason} · จำนวน × ราคา ลงยอดของบรรทัดไม่ได้ "
              + "(คงค่าตาม XML ไม่หารจำนวนจากยอด) — แก้บรรทัดนี้กับกระดาษก่อนอนุมัติ";
    }

    /// <summary>ตัดสินบรรทัดเดียว (ทางราคาก่อน VAT) — ดูกติกาที่หัวคลาส</summary>
    public static OcrEtaxLine Normalize(OcrEtaxLineFacts f)
    {
        if (f.BilledQuantity is not decimal q || f.GrossUnitPrice is not decimal g || f.NetAmount is not decimal net)
            return Undecided(f, ReasonMissing);
        if (q <= 0m || g < 0m || net < 0m) return Undecided(f, ReasonNegative);
        if (f.LineCharge is > 0m) return Undecided(f, ReasonCharge);
        var allowance = f.LineAllowance ?? 0m;
        if (allowance < 0m) return Undecided(f, ReasonNegative);

        var afterAllowance = R2(q * g) - allowance;
        var matchesNet = Math.Abs(afterAllowance - net) <= Tol;
        var matchesIncl = f.NetIncludingVatAmount is decimal ni && ni != net && Math.Abs(afterAllowance - ni) <= Tol;
        if (matchesNet == matchesIncl) return Undecided(f, matchesNet ? ReasonBoth : ReasonNeither);

        if (matchesNet)
        {
            // ราคาก่อน VAT อยู่แล้ว — จำนวน/ราคาตาม XML · ไม่มีส่วนลด = พฤติกรรมเดิม (เศษ 1 สตางค์ไปทางผลต่างปัดเศษตามเดิม)
            decimal? disc = allowance > 0m && R2(q * g) - net > 0m ? R2(q * g) - net : null;
            return new OcrEtaxLine(OcrEtaxPriceBasis.ExclusiveOfVat, q, g, disc, net, true);
        }

        // ราคารวม VAT — อัตราจากบรรทัด ไม่มีก็อนุมานจากยอดสองตัวของบรรทัดเอง (ยอดรวม VAT ÷ ยอดก่อน VAT)
        var rate = f.VatRatePercent is decimal vr && vr > 0m
            ? vr
            : net > 0m ? Math.Round((f.NetIncludingVatAmount!.Value / net - 1m) * 100m, 0, MidpointRounding.AwayFromZero) : 0m;
        if (rate <= 0m) return Undecided(f, ReasonNoRate);
        var unitEx = R2(g * 100m / (100m + rate));
        // ฝ่ายค้านรอบสอง (ข้อ 1): ราคาที่ถอด VAT แล้วปัด 2 ตำแหน่ง × จำนวน ไม่เท่ายอดก่อน VAT เสมอ — 7 × 10.00 (รวม VAT) ⇒ 9.35 × 7 = 65.45
        // แต่ XML 65.42 · 120 × 1.00 ⇒ 0.93 × 120 = 111.60 แต่ XML 112.15 · ถ้าไม่มีอะไรรับเศษนี้ ตอนเปิดแก้แล้วบันทึก
        // DocumentService.ComputeLineAmounts คิดใหม่เป็น จำนวน × ราคา แล้วยอดหลุดจาก XML ที่ลงนาม ⇒ บรรทัดต้องลงตัวพอดีเสมอ:
        // ราคาต้องไม่ทำให้ round(จำนวน × ราคา) ต่ำกว่ายอดก่อน VAT (ส่วนลดติดลบใช้ไม่ได้ — ComputeLineAmounts ไม่รับ) ⇒ ขยับราคาขึ้น
        // เป็นราคา 2 ตำแหน่งที่น้อยที่สุดที่ยังครอบยอด แล้ว "เศษจากการถอด VAT" เป็นส่วนลดของบรรทัด (มีส่วนลดบนกระดาษ = รวมกัน)
        if (R2(q * unitEx) < net)
            unitEx = Math.Max(unitEx, Math.Ceiling(net / q * 100m) / 100m);
        var grossEx = R2(q * unitEx);
        if (grossEx < net) return Undecided(f, ReasonNeither);   // ป้องกันไว้ — ตามคณิตศาสตร์เกิดไม่ได้
        decimal? discEx = grossEx - net > 0m ? grossEx - net : null;
        // ฝ่ายค้านรอบสาม: "เศษจากการถอด VAT" = ส่วนลดก่อน VAT ที่ได้ − ส่วนลดบนกระดาษที่ถอด VAT แล้ว · เกินเพดาน (1 บาท และ 0.5% ของบรรทัด)
        // ⇒ ราคา 2 ตำแหน่งบิดค่ามากเกินจะเรียกว่าเศษ ⇒ ติดธง ResidualOverCap ให้คนตรวจ
        var allowanceEx = allowance > 0m ? R2(allowance * 100m / (100m + rate)) : 0m;
        var residual = (discEx ?? 0m) - allowanceEx;
        // รอบ 6 (ใบ 2614501699 ข้อ 2): เดิมเกินเพดาน ⇒ Unknown แต่บรรทัดยังถูกคุ้มครองจากตัวแก้จำนวน ⇒ 150 × 1.00 − 0 ≠ 65.42 (แย่ทั้งสองทาง) ·
        // ตอนนี้: ฐานรวม VAT พิสูจน์แล้ว ⇒ ใช้ส่วนลดที่ทำให้บรรทัดลงตัว (ไม่ทิ้งบรรทัดที่ขัดกันเอง) + ธงให้ผู้เรียกเขียนหมายเหตุห้ามอนุมัติเอง
        var overCap = residual != 0m
            && (Math.Abs(residual) > MaxVatStripResidual || Math.Abs(residual) > net * MaxVatStripResidualShare);
        return new OcrEtaxLine(OcrEtaxPriceBasis.InclusiveOfVat, q, unitEx, discEx, net, true,
            PriceIncludesVat: false, VatStripResidual: residual != 0m ? residual : null, VatRate: rate, ResidualOverCap: overCap);
    }

    /// <summary>ชื่อ engine ของเส้น e-Tax XML (<c>OcrScanResult.OcrEngine</c>) — ตัวเดียวที่ตัวกันจำนวนระเบิด/การเขียนบรรทัดกลับ ใช้ตัดสินว่า
    /// จำนวนมาจากเอกสารที่ลงนามแล้ว</summary>
    public const string EtaxEngine = "EtaxXml";

    /// <summary>สแกนนี้มาจาก e-Tax XML ที่ฝังใน PDF ⇒ จำนวนทุกบรรทัดคือ <c>BilledQuantity</c> (ห้ามหารใหม่จากยอด)</summary>
    public static bool IsEtaxEngine(string? ocrEngine)
        => string.Equals(ocrEngine, EtaxEngine, StringComparison.Ordinal);

    /// <summary>รายการสแกนที่บันทึกไว้ (<c>ExtractedItemsJson</c>) มีบรรทัดที่ติดธง <c>QuantityFromEtaxXml</c> ไหม — สำเนาจากอัปไฟล์ซ้ำมี engine
    /// "Cached" แต่ยังเป็นบรรทัดของ e-Tax ⇒ ตัวเขียนกลับตอนอนุมัติต้องรักษาธงไว้ · JSON เสีย/ว่าง = false (ไม่ throw)</summary>
    public static bool ItemsJsonCarriesSignedQuantities(string? itemsJson)
    {
        if (string.IsNullOrWhiteSpace(itemsJson)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(itemsJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.ValueKind == System.Text.Json.JsonValueKind.Object
                    && el.TryGetProperty("QuantityFromEtaxXml", out var f)
                    && f.ValueKind == System.Text.Json.JsonValueKind.True)
                    return true;
            return false;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>ทุนต่อหน่วยจริงสำหรับนำเข้าสต็อก/ตัวตรวจสินทรัพย์ถาวร: บรรทัดที่มีส่วนลดของตัวเอง (พิสูจน์แล้ว) = ยอดหลังลด ÷ จำนวน
    /// (ทศนิยม 4 ตำแหน่ง — ทุนสต็อก ไม่ใช่ราคาบนใบกำกับ) · ไม่มีส่วนลด = ราคาต่อหน่วยเดิม · ฝ่ายค้านรอบสอง (ข้อ 3): เดิมเติมทุน = ราคาก่อนลด
    /// (34.58 แทน 26.2667) ⇒ มูลค่าสต็อกเกินยอดที่จ่ายจริง
    /// <para>บรรทัดราคารวม VAT ตามกระดาษ (<paramref name="priceIncludesVat"/>) = ยอดหลังลดถอด VAT แล้ว ÷ จำนวน (ฐานเดียวกับ
    /// <c>DocumentLine.Amount</c> ที่ DocumentService ใช้ลงทุนสต็อก) — 84.32 รวม VAT ⇒ 78.80 ÷ 3 = 26.2667 ไม่ใช่ 28.1067</para></summary>
    public static decimal? EffectiveUnitCost(decimal? quantity, decimal? unitPrice, decimal? amount, decimal? lineDiscount,
        bool priceIncludesVat = false, decimal? vatRate = null, decimal? fallbackRate = null)
    {
        if (priceIncludesVat)
        {
            // ราคารวม VAT: ต้องถอด VAT ก่อนหาร · ไม่รู้อัตรา (แม้อัตราหลักของใบ) ⇒ null — ไม่คืนราคารวม VAT เป็นทุน (ฝ่ายค้านรอบห้า ข้อ 4)
            var net = ExVatAmount(amount, true, vatRate, fallbackRate);
            return net is decimal n && quantity is decimal pq && pq > 0m
                ? Math.Round(n / pq, 4, MidpointRounding.AwayFromZero)
                : null;
        }
        if (ProvenLineDiscount(quantity, unitPrice, amount, lineDiscount) <= 0m) return unitPrice;
        var q = quantity ?? 1m;
        if (q <= 0m || amount is not decimal a) return unitPrice;
        return Math.Round(a / q, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>ส่วนลดรายบรรทัดที่ "อธิบายยอดได้จริง": round(จำนวน × ราคา) − ส่วนลด ≈ ยอด (±<see cref="Tol"/>) ⇒ คืนส่วนลด · ไม่งั้น 0
    /// <para>ตัวเดียวที่ตัวกันจำนวนระเบิด · ด่าน [Gateway] · ตัวสร้างบรรทัดเอกสาร ใช้ตัดสินว่า "จำนวน × ราคา ≠ ยอด" เป็นส่วนลด ไม่ใช่จำนวนผิด</para></summary>
    public static decimal ProvenLineDiscount(decimal? quantity, decimal? unitPrice, decimal? amount, decimal? lineDiscount)
    {
        if (lineDiscount is not decimal d || d <= 0m) return 0m;
        if (unitPrice is not decimal up || up <= 0m || amount is not decimal a) return 0m;
        var gross = R2((quantity ?? 1m) * up);
        if (d > gross) return 0m;
        return Math.Abs(gross - d - a) <= Tol ? d : 0m;
    }

    /// <summary>ยอดบรรทัดหลังผู้ใช้แก้จำนวน/ราคา/ส่วนลดในหน้ารีวิว: round(จำนวน × ราคา) − ส่วนลดบรรทัด · ส่วนลดเกินยอดก่อนลด ⇒ คืน
    /// <c>Discount = null</c> + <c>DiscountDropped = true</c> (ทิ้งส่วนลด — ยอดติดลบไม่มีจริง · ผู้เรียกต้องบอกผู้ใช้ ห้ามเงียบ)
    /// · เดิมคำนวณ round(จำนวน × ราคา) ตรง ๆ ⇒ บรรทัด e-Tax ที่มีส่วนลดกลับไปเป็นยอดก่อนลดเงียบ ๆ</summary>
    public static (decimal Amount, decimal? Discount, bool DiscountDropped) AmountAfterLineDiscount(decimal quantity, decimal unitPrice, decimal? lineDiscount)
    {
        var gross = R2(quantity * unitPrice);
        if (lineDiscount is decimal d && d > 0m)
        {
            var dr = R2(d);
            if (dr <= gross) return (gross - dr, dr, false);
            return (gross, (decimal?)null, true);
        }
        return (gross, (decimal?)null, false);
    }
}
