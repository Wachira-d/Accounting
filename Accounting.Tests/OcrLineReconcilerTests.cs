using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตัวเลขจริงจากผลตรวจ OCR 2026-09-05 (T2-05 · T1-09) — ทุกเคสต้องให้เอกสาร
/// ที่ยอดตรงกระดาษ และห้ามแต่งบรรทัด/ส่วนลดที่กระดาษไม่มี
/// </summary>
public class OcrLineReconcilerTests
{
    [Fact]
    public void ราคาแยกVATมาตรฐาน_ไม่ปรับอะไร()
    {
        var r = OcrLineReconciler.Classify(grossSum: 1000m, headerSubTotal: 1000m, headerVat: 70m, headerTotal: 1070m);
        Assert.Equal(OcrLineReconcileCase.LinesMatchSubTotal, r.Case);
        Assert.False(r.PricesIncludeVat);
        Assert.Equal(0m, r.DiscountPercent);
        Assert.Equal(0m, r.UnreconciledGap);
    }

    [Fact]
    public void ส่วนลด50บนกระดาษ_เดิมถูกตีเป็นราคารวมVATและแต่งบรรทัดผี_ตอนนี้เป็นส่วนลดบนฐานก่อนVAT()
    {
        // สินค้า 1,000 · ส่วนลด 50 · sub 950 · VAT 66.50 · total 1,016.50
        var r = OcrLineReconciler.Classify(1000m, 950m, 66.50m, 1016.50m, headerDiscount: 50m);
        Assert.Equal(OcrLineReconcileCase.DiscountOnSubTotal, r.Case);
        Assert.False(r.PricesIncludeVat);
        Assert.Equal(5m, r.DiscountPercent);
        Assert.Equal(950m, r.TargetLineSum);
        Assert.Equal(0m, r.UnreconciledGap);
    }

    [Fact]
    public void ส่วนลด200_เดิมคิดเทียบยอดรวมได้เอกสาร912_ตอนนี้บรรทัดรวม800ตรงกระดาษ()
    {
        // สินค้า 1,000 · ส่วนลด 200 · sub 800 · VAT 56 · total 856
        var r = OcrLineReconciler.Classify(1000m, 800m, 56m, 856m, headerDiscount: 200m);
        Assert.Equal(OcrLineReconcileCase.DiscountOnSubTotal, r.Case);
        Assert.Equal(20m, r.DiscountPercent);
        Assert.Equal(800m, r.TargetLineSum);
    }

    [Fact]
    public void ร้านอาหารลด3เปอร์เซ็นต์บนราคารวมVAT_ส่วนลดคิดบนฐานก่อนVAT_ไม่บวกVATซ้ำ()
    {
        // อาหาร 1,070 (รวม VAT) ลด 32.10 → total 1,037.90 · sub 970 · VAT 67.90
        // (เดิม: บรรทัดได้ 1,037.90 แล้วบวก VAT ซ้ำ → เอกสาร 1,105.80)
        var r = OcrLineReconciler.Classify(1070m, 970m, 67.90m, 1037.90m, headerDiscount: 100m);
        Assert.Equal(OcrLineReconcileCase.DiscountOnSubTotal, r.Case);
        Assert.Equal(970m, r.TargetLineSum);
    }

    [Fact]
    public void ราคารวมVATจริง_IKEA_ตั้งธงPricesIncludeVat_ไม่แต่งบรรทัด()
    {
        var r = OcrLineReconciler.Classify(1396m, 1304.67m, 91.33m, 1396m);
        Assert.Equal(OcrLineReconcileCase.PricesIncludeVat, r.Case);
        Assert.True(r.PricesIncludeVat);
        Assert.Equal(0m, r.UnreconciledGap);
    }

    [Fact]
    public void OCRอ่านบรรทัดขาด_ค่าขนส่ง50_ไม่แต่งบรรทัด_รายงานช่องว่าง()
    {
        // สินค้า 1,000 + ขนส่ง 50 ที่ OCR ไม่อ่าน → sub 1,050 · VAT 73.50 · total 1,123.50
        var r = OcrLineReconciler.Classify(1000m, 1050m, 73.50m, 1123.50m);
        Assert.Equal(OcrLineReconcileCase.LinesShort, r.Case);
        Assert.Equal(50m, r.UnreconciledGap);
        Assert.Equal(0m, r.DiscountPercent);
        Assert.Contains("บรรทัดขาด", r.Note);
    }

    [Fact]
    public void Σบรรทัดเกินยอดก่อนVATแต่กระดาษไม่บอกส่วนลด_ไม่เดา_ตีเป็นกำกวม()
    {
        // เดิม: ส่วนต่าง 16.50 ถูกแต่งเป็นบรรทัด "ค่าขนส่ง/บริการอื่น" ที่ไม่มีบนกระดาษ
        var r = OcrLineReconciler.Classify(1000m, 950m, 66.50m, 1016.50m, headerDiscount: 0m);
        Assert.Equal(OcrLineReconcileCase.Ambiguous, r.Case);
        Assert.False(r.PricesIncludeVat);
        Assert.Equal(0m, r.DiscountPercent);
        Assert.Equal(-50m, r.UnreconciledGap);
    }

    [Fact]
    public void บิลเงินสดไม่มีVAT_มีส่วนลดบนกระดาษ_คิดเทียบยอดรวม()
    {
        var r = OcrLineReconciler.Classify(500m, 0m, 0m, 450m, headerDiscount: 50m);
        Assert.Equal(OcrLineReconcileCase.DiscountOnTotal, r.Case);
        Assert.Equal(10m, r.DiscountPercent);
        Assert.Equal(450m, r.TargetLineSum);
    }

    [Fact]
    public void ไม่มีหัวใบ_ไม่ทำอะไร()
    {
        var r = OcrLineReconciler.Classify(500m, 0m, 0m, 0m);
        Assert.Equal(OcrLineReconcileCase.NoHeader, r.Case);
    }

    // ═══ รอบ 190 ข้อ 9 — เคส E: ราคารวม VAT + ส่วนลดท้ายบิล (ใบซูเปอร์มาร์เก็ต) ═══

    [Fact]
    public void ราคารวมVATและส่วนลดสมาชิก_เดิมกำกวม_ตอนนี้กระจายส่วนลดบนยอดรวม()
    {
        // 159 + 438 + 177 = 774 (รวม VAT) · ส่วนลด 38.70 · สุทธิ 735.30 · มูลค่าสินค้า 687.20 · VAT 48.10
        var r = OcrLineReconciler.Classify(774m, 687.20m, 48.10m, 735.30m, headerDiscount: 38.70m);
        Assert.Equal(OcrLineReconcileCase.DiscountOnTotalInclVat, r.Case);
        Assert.True(r.PricesIncludeVat);
        Assert.Equal(5m, r.DiscountPercent);
        Assert.Equal(735.30m, r.TargetLineSum);
        Assert.Equal(0m, r.UnreconciledGap);
    }

    [Fact]
    public void ราคารวมVATและส่วนลด_แต่ตัวอ่านส่วนลดไม่เห็น_ยังเป็นกำกวม_ไม่เดาส่วนลดจากส่วนต่าง()
    {
        // ทิศตรงข้าม: ส่วนต่าง 38.70 ห้ามถูกตีเป็นส่วนลดเอง — ต้องมีตัวเลขบนกระดาษ
        var r = OcrLineReconciler.Classify(774m, 687.20m, 48.10m, 735.30m, headerDiscount: 0m);
        Assert.Equal(OcrLineReconcileCase.Ambiguous, r.Case);
        Assert.Equal(0m, r.DiscountPercent);
    }

    [Fact]
    public void ส่วนลดที่อ่านได้ไม่อธิบายส่วนต่าง_ไม่เข้าเคสE()
    {
        // ส่วนลด 20 บนกระดาษ (อ่านผิดแถว) — 774 − 20 ≠ 735.30 ⇒ ยังตัดสินไม่ได้
        var r = OcrLineReconciler.Classify(774m, 687.20m, 48.10m, 735.30m, headerDiscount: 20m);
        Assert.Equal(OcrLineReconcileCase.Ambiguous, r.Case);
    }

    [Fact]
    public void ใบร้านวัสดุลดท้ายบิล_ยังเป็นเคสCเหมือนเดิม_เคสEไม่แย่ง()
    {
        var r = OcrLineReconciler.Classify(1395m, 1325.25m, 92.77m, 1418.02m, headerDiscount: 69.75m);
        Assert.Equal(OcrLineReconcileCase.DiscountOnSubTotal, r.Case);
        Assert.False(r.PricesIncludeVat);
        Assert.Equal(1325.25m, r.TargetLineSum);
    }

    // ── รอบ 195 (ใบ Scommerce): บรรทัดยอด 0 ไม่ได้ % ส่วนลดที่ไม่มีบนกระดาษ ──────────────────────────
    [Fact]
    public void ส่วนลดท้ายบิล_บรรทัดค่าส่ง0บาทไม่ได้เปอร์เซ็นต์_บรรทัดที่มียอดได้เท่าเดิม()
    {
        var r = OcrLineReconciler.Classify(4912.15m, 4695.33m, 328.67m, 5024.00m, headerDiscount: 216.82m);
        Assert.Equal(OcrLineReconcileCase.DiscountOnSubTotal, r.Case);
        Assert.Equal(4.41m, r.DiscountPercent);
        Assert.Equal(0m, OcrLineReconciler.LineDiscountPercent(r.DiscountPercent, 0m));       // ค่าจัดส่ง 0.00
        Assert.Equal(4.41m, OcrLineReconciler.LineDiscountPercent(r.DiscountPercent, 4912.15m)); // ทิศตรงข้าม: สินค้าได้เท่าเดิม
        Assert.Equal(0m, OcrLineReconciler.LineDiscountPercent(0m, 4912.15m));                  // ไม่มีส่วนลด = 0 ทุกบรรทัด
    }

    // ── ทีม T 2026-10-26 เคส G (สังเคราะห์ · ภาพสะท้อนของเคส F — docs/OCR_TOTALS_MATRIX.md ช่อง "ก่อน VAT × ท้ายบิลรวม VAT") ──────
    [Fact]
    public void เคสG_ราคาก่อนVAT_ส่วนลดท้ายบิลพิมพ์รวมVAT576_แปลงเป็น538_32ก่อนVAT_กระจายแบบเคสC()
    {
        // สังเคราะห์: 2 × 920 + 170 = 2,010 ก่อน VAT · ส่วนลด 576.00 รวม VAT (= 538.32 ก่อน VAT) · ฐาน 1,471.68 · VAT 103.02 · รวม 1,574.70
        Assert.Equal((decimal?)538.32m, OcrLineReconciler.InclVatDiscountOnExclusiveLines(2010.00m, 1471.68m, 103.02m, 1574.70m, 576.00m));
        var r = OcrLineReconciler.Classify(2010.00m, 1471.68m, 103.02m, 1574.70m, headerDiscount: 576.00m);
        Assert.Equal(OcrLineReconcileCase.DiscountInclVatOnExclusivePrices, r.Case);
        Assert.Equal((false, 26.78m, (decimal?)1471.68m, 0m), (r.PricesIncludeVat, r.DiscountPercent, r.TargetLineSum, r.UnreconciledGap));
        Assert.Equal((decimal?)538.32m, r.BillDiscountPreVat);   // ข้อ 159: ลง BillDiscountAmount (ก่อน VAT) ไม่กระจาย — ตัวเลขข้างล่างคือสูตรกระจายเดิม (helper ยังใช้กับเคส C)
        // ตัวกระจายของตัวสร้างบรรทัด (เป้า = ฐาน) + ส่วนลดบาทต่อบรรทัด = round(จำนวน × ราคา) − ยอดหลังลด ⇒ Σ = 1,471.68 พอดี
        var first = Math.Round(1471.68m * 1840.00m / 2010.00m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(new[] { 1347.21m, 124.47m }, new[] { first, 1471.68m - first });
        Assert.Equal((26.78m, 492.79m, (decimal?)null), OcrLineReconciler.LineDiscount(r.DiscountPercent, 1840.00m, true, first, 2m));
        Assert.Equal((26.78m, 45.53m, (decimal?)null), OcrLineReconciler.LineDiscount(r.DiscountPercent, 170.00m, true, 1471.68m - first, 1m));
        // ป้ายสลับ (Sub Total = ยอดสุทธิ) ผ่าน OcrHeaderAmounts.Normalize ก่อน ⇒ คำตอบเดียวกัน
        Assert.Equal(OcrLineReconcileCase.DiscountInclVatOnExclusivePrices, OcrLineReconciler.Classify(2010.00m, 1574.70m, 103.02m, 1471.68m, 576.00m).Case);
    }

    [Fact]
    public void เคสG_ทิศตรงข้าม_ส่วนลดก่อนVATยังเคสC_ไม่เข้าสมการยังAmbiguous_VAT0หรือไม่ใช่7เปอร์เซ็นต์ไม่เข้า_บรรทัดขาดยังLinesShort_เคสFไม่ถูกแย่ง()
    {
        Assert.Equal(OcrLineReconcileCase.DiscountOnSubTotal, OcrLineReconciler.Classify(2010.00m, 1471.68m, 103.02m, 1574.70m, 538.32m).Case);
        Assert.Equal(OcrLineReconcileCase.Ambiguous, OcrLineReconciler.Classify(2010.00m, 1471.68m, 103.02m, 1574.70m, 500.00m).Case);
        Assert.Equal(OcrLineReconcileCase.Ambiguous, OcrLineReconciler.Classify(2010.00m, 1340.19m, 93.81m, 1434.00m, 500.00m).Case);   // vector ของทีม R ไม่เปลี่ยน
        Assert.Null(OcrLineReconciler.InclVatDiscountOnExclusiveLines(2010.00m, 1434.00m, 0m, 1434.00m, 576.00m));                          // VAT 0 — เคส C รับไปก่อนแล้ว
        Assert.Equal(OcrLineReconcileCase.DiscountOnSubTotal, OcrLineReconciler.Classify(2010.00m, 1434.00m, 0m, 1434.00m, 576.00m).Case);
        Assert.Null(OcrLineReconciler.InclVatDiscountOnExclusiveLines(2010.00m, 1471.68m, 90.00m, 1561.68m, 576.00m));                      // VAT ≠ 7% ของฐาน
        Assert.Equal(OcrLineReconcileCase.LinesShort, OcrLineReconciler.Classify(1000.00m, 1471.68m, 103.02m, 1574.70m, 576.00m).Case); // Σ < ฐาน ไม่ถูกแตะ
        Assert.Equal(OcrLineReconcileCase.DiscountPreVatOnInclusivePrices, OcrLineReconciler.Classify(2010.00m, 1340.19m, 93.81m, 1434.00m, 538.32m).Case);
    }
}
