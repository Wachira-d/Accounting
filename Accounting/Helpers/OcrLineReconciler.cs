using System;

namespace Accounting.Helpers;

/// <summary>ผลการจำแนกว่า "ผลรวมบรรทัดที่ OCR อ่านได้" สัมพันธ์กับหัวใบอย่างไร</summary>
public enum OcrLineReconcileCase
{
    /// <summary>ไม่มีหัวใบ/ไม่มีบรรทัดพอจะเทียบ — ไม่ปรับอะไร</summary>
    NoHeader = 0,
    /// <summary>(B) Σ บรรทัด ≈ ยอดก่อน VAT — ราคาแยก VAT มาตรฐาน ไม่ต้องทำอะไร</summary>
    LinesMatchSubTotal = 1,
    /// <summary>(A) Σ บรรทัด ≈ ยอดรวม VAT — ราคาต่อหน่วยรวม VAT แล้ว (IKEA/ค้าปลีก)</summary>
    PricesIncludeVat = 2,
    /// <summary>(C) มีส่วนลดท้ายบิลบนกระดาษ และ Σ บรรทัด − ส่วนลด ≈ ยอดก่อน VAT</summary>
    DiscountOnSubTotal = 3,
    /// <summary>(C-legacy) ใบไม่มียอดก่อน VAT (บิลไม่มี VAT) และ Σ บรรทัด > ยอดรวม — ถือเป็นส่วนลด</summary>
    DiscountOnTotal = 4,
    /// <summary>(D) Σ บรรทัด &lt; ยอดก่อน VAT — OCR อ่านบรรทัดขาด ปล่อยให้คนเติม (ห้ามแต่งบรรทัด)</summary>
    LinesShort = 5,
    /// <summary>Σ บรรทัด &gt; ยอดก่อน VAT แต่ไม่ตรงยอดรวม VAT และกระดาษไม่บอกส่วนลด —
    /// ตัดสินไม่ได้ว่า "ส่วนลดที่ไม่ได้พิมพ์" หรือ "OCR อ่านตัวเลขเพี้ยน" → ไม่แต่งตัวเลข ให้คนดู</summary>
    Ambiguous = 6,
}

/// <summary>ผลลัพธ์: ปรับ document อย่างไร + ช่องว่างที่ต้องบอกผู้ใช้</summary>
/// <param name="Case">เคสที่จำแนกได้</param>
/// <param name="PricesIncludeVat">ตั้ง <c>Document.PricesIncludeVat</c> หรือไม่</param>
/// <param name="DiscountPercent">ส่วนลด % ที่ต้องใส่ทุกบรรทัด (0 = ไม่มี) — คิดจากฐาน Σ บรรทัด</param>
/// <param name="TargetLineSum">ยอดที่ Σ บรรทัดหลังปรับควรเท่ากับ (null = ไม่ปรับ)</param>
/// <param name="UnreconciledGap">ส่วนต่างที่ระบบ<b>ไม่</b>แต่งให้ (บวก = กระดาษมากกว่าบรรทัด) — 0 เมื่อลงตัว</param>
/// <param name="Note">ข้อความสำหรับ ProcessingNotes / ป้ายเตือน (ภาษาไทย)</param>
public sealed record OcrLineReconcileResult(
    OcrLineReconcileCase Case,
    bool PricesIncludeVat,
    decimal DiscountPercent,
    decimal? TargetLineSum,
    decimal UnreconciledGap,
    string Note);

/// <summary>
/// ตัวจำแนกความสัมพันธ์ "ผลรวมบรรทัด ↔ หัวใบ" (pure, ไม่มี I/O) ใช้ตอนสร้าง
/// <c>DocumentLine</c> จากผลสแกน — แยกออกจาก <c>OcrService.CreateDocumentFromScanAsync</c>
/// เพื่อให้ทดสอบด้วยตัวเลขจริงได้
///
/// ═══ ทำไมต้องมี (บั๊กจริง — ผลตรวจ OCR รอบ 2026-09-05 T2-05 + T1-09) ═══
/// ตรรกะเดิมเทียบ "Σ บรรทัด (ก่อน VAT)" กับ "ยอดรวม (หลัง VAT)" ⇒
///   • ใบมีส่วนลด 50 บนสินค้า 1,000 (sub 950 · VAT 66.50 · total 1,016.50): Σ บรรทัด 1,000
///     ตกอยู่ระหว่าง sub กับ total → ถูกตีเป็น "ราคารวม VAT" แล้ว**แต่งบรรทัดผี** 16.50
///     "ค่าขนส่ง/บริการอื่น" ที่ไม่มีบนกระดาษ (ค่าที่แต่งขึ้นเพื่อให้โค้ดเดินต่อ)
///   • ร้านอาหารลด 3% ท้ายบิล: ส่วนลดถูกคิดเทียบ total → บรรทัดได้ 1,037.90 แล้วบวก VAT
///     ซ้ำ → เอกสาร 1,105.80 ≠ กระดาษ 1,037.90 · ยอด "ดูสมเหตุสมผล" จึงเงียบสนิท
/// กติกาใหม่: เทียบกับ<b>ยอดก่อน VAT</b>เป็นหลัก · ส่วนลดต้องมี<b>หลักฐานบนกระดาษ</b>
/// (ช่อง "ส่วนลด") ไม่ใช่อนุมานจากส่วนต่าง · ตัดสินไม่ได้ = บอกว่าไม่ได้ (Ambiguous/LinesShort)
/// แล้วให้หน้า review เตือน — ไม่แต่งบรรทัด ไม่แต่งส่วนลด
/// </summary>
public static class OcrLineReconciler
{
    /// <summary>ค่าเผื่อปัดเศษ (บาท) — OCR อ่านสตางค์เพี้ยนได้ แต่ไม่ควรเกิน 1 บาท</summary>
    public const decimal Tolerance = 1m;

    /// <param name="grossSum">Σ (qty × unitPrice) หรือ amount ของบรรทัดที่ OCR อ่านได้ (ก่อนส่วนลด/VAT)</param>
    /// <param name="headerSubTotal">ยอดก่อน VAT บนหัวใบ (0 = ไม่มี/อ่านไม่ได้)</param>
    /// <param name="headerVat">VAT บนหัวใบ (0 = ไม่มี)</param>
    /// <param name="headerTotal">ยอดรวมสุทธิบนหัวใบ (0 = ไม่มี)</param>
    /// <param name="headerDiscount">ส่วนลดท้ายบิลที่<b>พิมพ์อยู่บนกระดาษ</b> (0 = กระดาษไม่บอก)</param>
    public static OcrLineReconcileResult Classify(
        decimal grossSum, decimal headerSubTotal, decimal headerVat, decimal headerTotal,
        decimal headerDiscount = 0m)
    {
        if (grossSum <= 0m || (headerSubTotal <= 0m && headerTotal <= 0m))
            return new(OcrLineReconcileCase.NoHeader, false, 0m, null, 0m, "");

        // (B) ราคาแยก VAT มาตรฐาน — Σ บรรทัดตรงยอดก่อน VAT
        if (headerSubTotal > 0m && Math.Abs(grossSum - headerSubTotal) <= Tolerance)
            return new(OcrLineReconcileCase.LinesMatchSubTotal, false, 0m, null, 0m, "");

        // (C) ส่วนลดที่กระดาษพิมพ์ไว้จริง: Σ บรรทัด − ส่วนลด ≈ ยอดก่อน VAT
        if (headerDiscount > 0m && headerSubTotal > 0m
            && Math.Abs(grossSum - headerDiscount - headerSubTotal) <= Tolerance)
        {
            var pct = Math.Round(headerDiscount / grossSum * 100m, 2, MidpointRounding.AwayFromZero);
            return new(OcrLineReconcileCase.DiscountOnSubTotal, false, pct, headerSubTotal, 0m,
                $"ส่วนลดท้ายบิล {headerDiscount:N2} ({pct:0.##}%) กระจายลงทุกบรรทัด");
        }

        // (A) ราคาต่อหน่วยรวม VAT — Σ บรรทัดตรงยอดรวมสุทธิ (ต้องมี VAT จริง ไม่งั้นคือ B)
        if (headerVat > 0m && headerTotal > 0m && Math.Abs(grossSum - headerTotal) <= Tolerance)
            return new(OcrLineReconcileCase.PricesIncludeVat, true, 0m, null, 0m,
                "ราคาต่อหน่วยบนกระดาษรวม VAT แล้ว — ระบบถอด VAT ออกจากยอดบรรทัด");

        // ใบไม่มียอดก่อน VAT (บิลเงินสด/ใบเสร็จไม่มี VAT): เทียบกับยอดรวมโดยตรง
        if (headerSubTotal <= 0m)
        {
            if (grossSum > headerTotal + Tolerance)
            {
                if (headerDiscount > 0m && Math.Abs(grossSum - headerDiscount - headerTotal) <= Tolerance)
                {
                    var pct = Math.Round(headerDiscount / grossSum * 100m, 2, MidpointRounding.AwayFromZero);
                    return new(OcrLineReconcileCase.DiscountOnTotal, false, pct, headerTotal, 0m,
                        $"ส่วนลดท้ายบิล {headerDiscount:N2} ({pct:0.##}%) กระจายลงทุกบรรทัด");
                }
                return new(OcrLineReconcileCase.Ambiguous, false, 0m, null, headerTotal - grossSum,
                    $"Σ บรรทัด {grossSum:N2} มากกว่ายอดรวม {headerTotal:N2} แต่กระดาษไม่ระบุส่วนลด — ตรวจสอบบรรทัด");
            }
            if (grossSum < headerTotal - Tolerance)
                return new(OcrLineReconcileCase.LinesShort, false, 0m, null, headerTotal - grossSum,
                    $"Σ บรรทัด {grossSum:N2} น้อยกว่ายอดรวม {headerTotal:N2} — OCR อาจอ่านบรรทัดขาด {headerTotal - grossSum:N2}");
            return new(OcrLineReconcileCase.LinesMatchSubTotal, false, 0m, null, 0m, "");
        }

        // (D) OCR อ่านบรรทัดขาด — ห้ามแต่งบรรทัดเติม
        if (grossSum < headerSubTotal - Tolerance)
            return new(OcrLineReconcileCase.LinesShort, false, 0m, null, headerSubTotal - grossSum,
                $"Σ บรรทัด {grossSum:N2} น้อยกว่ายอดก่อน VAT {headerSubTotal:N2} — OCR อาจอ่านบรรทัดขาด {headerSubTotal - grossSum:N2}");

        // Σ บรรทัด > ยอดก่อน VAT แต่ไม่ตรงยอดรวม และกระดาษไม่บอกส่วนลด → ไม่เดา
        return new(OcrLineReconcileCase.Ambiguous, false, 0m, null, headerSubTotal - grossSum,
            $"Σ บรรทัด {grossSum:N2} มากกว่ายอดก่อน VAT {headerSubTotal:N2} ({grossSum - headerSubTotal:N2}) "
            + "แต่ไม่ตรงยอดรวม VAT และกระดาษไม่ระบุส่วนลด — ตรวจสอบราคาต่อหน่วย/ส่วนลด");
    }
}
