using System.Globalization;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **Golden ระดับบรรทัด** (2026-10-09 — ratchet ของเส้นกระดาษ): สำหรับทุกใบใน <see cref="OcrReplayHarness.Corpus"/>
/// ล็อก "บรรทัดเอกสารที่ตัวสร้างจะเขียนจริง" (ยอดก่อน VAT · อัตรา · VAT · ราคาต่อหน่วย · ส่วนลด % / บาท · หัก ณ ที่จ่าย ต่อบรรทัด
/// + เคสกระทบยอด + ธงราคารวม VAT + ช่องว่าง [Σ-GAP] + ผลต่างปัดเศษ) ด้วยตัวตัดสิน pure ชุดเดียวกับ <c>BuildScanLinesAsync</c>
///
/// <para>ของเดิม (<see cref="OcrReplayGoldenTests"/>) ล็อกแค่ช่องหัวใบ ⇒ การแก้ที่ทำให้ "ยอดรวมยังตรงแต่บรรทัดเพี้ยน"
/// (VAT เฉลี่ยผิดบรรทัด · ส่วนลดหาย · ราคาต่อหน่วยไม่ใช่ของกระดาษ) ไม่มีอะไรฟ้อง — ทั้งที่บรรทัดคือสิ่งที่กลายเป็นรายงาน §87 และ JE</para>
///
/// <para>รูปแต่ละบรรทัด: <c>ยอดก่อนVAT|อัตรา|VAT|ราคาต่อหน่วย|ส่วนลด%|ส่วนลดบาท|หัก ณ ที่จ่าย</c> คั่นบรรทัดด้วย <c>;</c> —
/// ทุกตัวเลขคิดมือจากกระดาษแล้ว (คอมเมนต์บอกที่มา) · แถวที่ล้ม = การแก้ครั้งนี้เปลี่ยนคำตอบของใบนั้น ต้องอธิบายได้ก่อนแก้ตัวเลข
/// (กฎเหล็ก #4 H) · <c>tools/ocr_golden_corpus_check.py</c> บังคับว่าใบใหม่ทุกใบใน <see cref="OcrPaperSamples"/> ต้องมีแถวที่นี่</para>
/// </summary>
public class OcrReplayLineGoldenTests
{
    private static string? Val(string paper, string field)
        => OcrReplayHarness.Run().Single(a => a.Paper == paper && a.Field == field).Value;

    // ── ใบที่มีรายการ: ราคารวม VAT (เคส A) ─────────────────────────────────────────────────────────────

    [Fact]
    public void WinePro_ราคารวมVAT_ถอดVATรายบรรทัดด้วยการเฉลี่ยหัวใบ_บรรทัดถุงไวน์ศูนย์คงศูนย์()
    {
        // 524 + 3,069 + 0 = 3,593 = ยอดรวม ⇒ เคส A · VAT 235.06 เฉลี่ยตามสัดส่วน: 34.28 · 200.78 · เศษ 0.00
        // ยอดก่อน VAT = 524 − 34.28 = 489.72 · 3,069 − 200.78 = 2,868.22 · Σ = 3,357.94 = ฐานบนกระดาษ
        Assert.Equal("Items", Val("winepro-vat-included", "LineMode"));
        Assert.Equal("PricesIncludeVat", Val("winepro-vat-included", "ReconCase"));
        Assert.Equal("true", Val("winepro-vat-included", "PricesIncludeVat"));
        Assert.Equal(
            "489.72|7|34.28|524.00|0|0.00|0.00;2868.22|7|200.78|255.75|0|0.00|0.00;0.00|7|0.00|0.00|0|0.00|0.00",
            Val("winepro-vat-included", "Lines"));
        Assert.Equal("ok", Val("winepro-vat-included", "IntegrityGaps"));
        Assert.Equal("0.00", Val("winepro-vat-included", "RoundingAdjustment"));
    }

    [Fact]
    public void ใบค้าส่งผสม_บรรทัดN_ยกเว้นไม่ได้VAT_บรรทัดV_เฉลี่ยVAT28_ฐานรวม764()
    {
        // สัญลักษณ์ V/N ตัดสินอัตรา (−1,−1,−1,7,7,7) · VAT 28 เฉลี่ยเฉพาะบรรทัด V (104/105/219 จาก 428): 6.80 · 6.87 · เศษ 14.33
        // ยอดก่อน VAT ของบรรทัด V: 97.20 · 98.13 · 204.67 (Σ 400.00) · บรรทัด N คงยอดเต็ม (364.00) · 764 + 28 = 792 ✓
        Assert.Equal("PricesIncludeVat", Val("wholesale-mixed-vat", "ReconCase"));
        Assert.Equal(
            "125.00|-1|0.00|125.00|0|0.00|0.00;189.00|-1|0.00|189.00|0|0.00|0.00;50.00|-1|0.00|25.00|0|0.00|0.00;"
            + "97.20|7|6.80|52.00|0|0.00|0.00;98.13|7|6.87|35.00|0|0.00|0.00;204.67|7|14.33|219.00|0|0.00|0.00",
            Val("wholesale-mixed-vat", "Lines"));
        Assert.Equal("ok", Val("wholesale-mixed-vat", "IntegrityGaps"));
    }

    [Fact]
    public void ใบU_Shopee_บรรทัดเดียวราคารวมVAT_ฐาน500_93_ส่วนลดพิเศษหลังใบกำกับไม่แตะบรรทัด()
    {
        Assert.Equal("PricesIncludeVat", Val("uptoyou-shopee-pay-not-total", "ReconCase"));
        Assert.Equal("500.93|7|35.07|268.00|0|0.00|0.00", Val("uptoyou-shopee-pay-not-total", "Lines"));
        Assert.Equal("ok", Val("uptoyou-shopee-pay-not-total", "IntegrityGaps"));
        Assert.Equal("0.00", Val("uptoyou-shopee-pay-not-total", "RoundingAdjustment"));
    }

    // ── ใบที่มีรายการ: ส่วนลดท้ายบิล (เคส C / E) ─────────────────────────────────────────────────────────

    [Fact]
    public void ใบS_Lazada_ส่วนลดก่อนVAT_กระจายลงบรรทัดนมผง_ค่าส่งศูนย์ไม่ได้ส่วนลด_เศษปัดราคาต่อหน่วย1สตางค์ไปหัวเอกสาร()
    {
        // Σ 4,912.15 − 216.82 = 4,695.33 = ฐาน ⇒ เคส C 4.41% · บรรทัดนมผงรับทั้งก้อน (ค่าส่ง 0 ไม่ได้ %) · ส่วนลดบาท 216.82
        // 4 × 1,228.04 = 4,912.16 ≠ 4,912.15 ที่พิมพ์ (ต่าง 0.01 = เศษปัด) ⇒ บรรทัดโต 0.01 เป็น 4,695.34 · RoundingAdjustment −0.01
        // ⇒ เปิดแก้แล้วบันทึก round(4 × 1,228.04) − 216.82 = 4,695.34 ตรงบรรทัด และ SubTotal = 4,695.34 − 0.01 = 4,695.33 ตรงกระดาษ
        Assert.Equal("DiscountOnSubTotal", Val("scommerce-lazada-prevat-discount", "ReconCase"));
        Assert.Equal("false", Val("scommerce-lazada-prevat-discount", "PricesIncludeVat"));
        Assert.Equal("4695.34|7|328.67|1228.04|4.41|216.82|0.00;0.00|7|0.00|0.00|0|0.00|0.00",
            Val("scommerce-lazada-prevat-discount", "Lines"));
        Assert.Equal("ok", Val("scommerce-lazada-prevat-discount", "IntegrityGaps"));
        Assert.Equal("-0.01", Val("scommerce-lazada-prevat-discount", "RoundingAdjustment"));
    }

    [Fact]
    public void ใบร้านวัสดุ_ลด5เปอร์เซ็นต์ก่อนVAT_กระจายตามสัดส่วน_ส่วนลดบาทรวม69_75_VATเฉลี่ยรวม92_77()
    {
        // 1,395 − 69.75 = 1,325.25 ⇒ เคส C 5% · กระจาย: 351.50 · 845.50 · เศษ 128.25 (Σ 1,325.25) · ส่วนลดบาท 18.50 + 44.50 + 6.75 = 69.75
        // VAT 92.77 เฉลี่ย: 24.61 (92.77 × 351.5/1325.25 = 24.605 ปัดขึ้น) · 59.19 · เศษ 8.97 · ราคาต่อหน่วยคงราคาเต็มตามกระดาษ
        Assert.Equal("Items", Val("hardware-bill-discount", "LineMode"));
        Assert.Equal("DiscountOnSubTotal", Val("hardware-bill-discount", "ReconCase"));
        Assert.Equal("(ไม่ใช้)", Val("hardware-bill-discount", "LineVatRates"));
        Assert.Equal("AllStandard7:7,7,7", Val("hardware-bill-discount", "LineVatPlan"));
        Assert.Equal(
            "351.50|7|24.61|185.00|5|18.50|0.00;845.50|7|59.19|890.00|5|44.50|0.00;128.25|7|8.97|45.00|5|6.75|0.00",
            Val("hardware-bill-discount", "Lines"));
        Assert.Equal("ok", Val("hardware-bill-discount", "IntegrityGaps"));
        Assert.Equal("0.00", Val("hardware-bill-discount", "RoundingAdjustment"));
    }

    [Fact]
    public void ใบซูเปอร์มาร์เก็ต_ราคารวมVATลดสมาชิก5เปอร์เซ็นต์_เคสE_ถอดVATจากยอดหลังลด_ฐานรวม687_20()
    {
        // 774 − 38.70 = 735.30 = ยอดรวม (ไม่ใช่ฐาน) ⇒ เคส E 5% ราคารวม VAT · กระจาย: 151.05 · 416.10 · เศษ 168.15
        // VAT 48.10 เฉลี่ย: 9.88 · 27.22 · เศษ 11.00 · ยอดก่อน VAT: 141.17 · 388.88 · 157.15 (Σ 687.20 = มูลค่าสินค้าบนกระดาษ)
        Assert.Equal("DiscountOnTotalInclVat", Val("supermarket-member-discount", "ReconCase"));
        Assert.Equal("true", Val("supermarket-member-discount", "PricesIncludeVat"));
        Assert.Equal("(ไม่ใช้)", Val("supermarket-member-discount", "LineVatRates"));
        Assert.Equal("AllStandard7:7,7,7", Val("supermarket-member-discount", "LineVatPlan"));
        Assert.Equal(
            "141.17|7|9.88|159.00|5|7.95|0.00;388.88|7|27.22|219.00|5|21.90|0.00;157.15|7|11.00|59.00|5|8.85|0.00",
            Val("supermarket-member-discount", "Lines"));
        Assert.Equal("ok", Val("supermarket-member-discount", "IntegrityGaps"));
    }

    // ── ใบที่ไม่มีรายการ: ตารางกลุ่มภาษี / บรรทัดสรุป ─────────────────────────────────────────────────

    [Fact]
    public void ใบM_Makro_หน้า3_ไม่มีรายการ_บรรทัดสรุปต่อกลุ่มภาษีตามตารางรหัส_ภพ()
    {
        Assert.Equal("VatGroups", Val("makro-page3-total-first", "LineMode"));
        Assert.Null(Val("makro-page3-total-first", "ReconCase"));
        Assert.Equal("6260.00|-1|0.00|6260.00|0|0.00|0.00;16403.97|7|1148.28|16403.97|0|0.00|0.00",
            Val("makro-page3-total-first", "Lines"));
        Assert.Equal("ok", Val("makro-page3-total-first", "IntegrityGaps"));
    }

    [Fact]
    public void ใบที่ไม่มีรายการและไม่มีตาราง_บรรทัดสรุปใบเดียวจากหัวใบ()
    {
        Assert.Equal("Summary", Val("luckyway-swapped", "LineMode"));
        Assert.Equal("1000.00|7|70.00|1000.00|0|0.00|0.00", Val("luckyway-swapped", "Lines"));   // หลังสลับป้ายกลับ
        Assert.Equal("1000.00|0|0.00|1000.00|0|0.00|0.00", Val("deposit-form-row-zero", "Lines"));
        Assert.Equal("50000.00|0|0.00|50000.00|0|0.00|0.00", Val("deposit-real", "Lines"));
        Assert.Equal("100000.00|0|0.00|100000.00|0|0.00|0.00", Val("export-zero-rated", "Lines"));
        Assert.Equal("20000.00|7|1400.00|20000.00|0|0.00|0.00", Val("service-wht-absent", "Lines"));
        Assert.Equal("0.00|0|0.00|0.00|0|0.00|0.00", Val("buyer-name-truncated", "Lines"));   // ใบที่ไม่มียอดเลย — ไม่แต่งอะไร
        foreach (var paper in new[] { "luckyway-swapped", "deposit-form-row-zero", "deposit-real", "export-zero-rated",
                     "service-wht-absent", "buyer-name-truncated" })
            Assert.Equal("ok", Val(paper, "IntegrityGaps"));
    }

    [Fact]
    public void ใบบริการที่กระดาษพิมพ์ส่วนหัก_บรรทัดสรุปได้หัก_ณ_ที่จ่าย300_จากฐาน10000()
        => Assert.Equal("10000.00|7|700.00|10000.00|0|0.00|300.00", Val("service-wht-printed", "Lines"));

    [Fact]
    public void Makro951_49_ไม่มีรายการ_บรรทัดสรุป7เปอร์เซ็นต์_ด่านΣฟ้องว่าVATไม่ใช่7เปอร์เซ็นต์ของยอด_ไม่แต่งตัวเลข()
    {
        // ใบผสม VAT/ยกเว้นที่ไฟล์ไม่มีรายการ: 7% × 951 = 66.57 ≠ 49 ⇒ [Σ-GAP] VatRateMismatch ให้คนแยกบรรทัด — ยอดคงตามกระดาษ
        Assert.Equal("Summary", Val("makro-correct", "LineMode"));
        Assert.Equal("951.00|7|49.00|951.00|0|0.00|0.00", Val("makro-correct", "Lines"));
        Assert.Equal("VatRateMismatch", Val("makro-correct", "IntegrityGaps"));
    }

    // ── ตัวเครื่องมือ: ทุกใบต้องมีแถวระดับบรรทัด · ไม่มีใบไหนได้ส่วนลดบาทติดลบ · ใบมีรายการ Σ ก่อน VAT + VAT = ยอดรวมเมื่อ ok ──

    [Fact]
    public void ทุกใบในชุด_มีแถวระดับบรรทัดครบ_และไม่มีใบไหนที่ok_แล้วยอดบรรทัดไม่ปิดยอดรวม()
    {
        var all = OcrReplayHarness.Run();
        foreach (var p in OcrReplayHarness.Corpus)
        {
            var mode = all.Single(a => a.Paper == p.Name && a.Field == "LineMode").Value;
            Assert.Contains(mode, new[] { "Items", "VatGroups", "Summary" });
            var lines = all.Single(a => a.Paper == p.Name && a.Field == "Lines").Value!;
            Assert.NotEmpty(lines);
            foreach (var row in lines.Split(';'))
            {
                var cols = row.Split('|');
                Assert.Equal(7, cols.Length);
                Assert.True(decimal.Parse(cols[5], CultureInfo.InvariantCulture) >= 0m, $"{p.Name}: ส่วนลดบาทติดลบ ({row})");
            }
            // ใบที่ด่าน Σ บอกว่า ok: Σ (ยอดก่อน VAT + VAT) + ผลต่างปัดเศษ ต้องเท่ายอดรวมที่ยึด (ไม่งั้นด่านพูดไม่ตรงตัวเลขของตัวเอง)
            if (all.Single(a => a.Paper == p.Name && a.Field == "IntegrityGaps").Value == "ok"
                && all.Single(a => a.Paper == p.Name && a.Field == "AnchorTotal").Value is string totalText)
            {
                var sum = lines.Split(';').Sum(r => decimal.Parse(r.Split('|')[0], CultureInfo.InvariantCulture) + decimal.Parse(r.Split('|')[2], CultureInfo.InvariantCulture))
                          + decimal.Parse(all.Single(a => a.Paper == p.Name && a.Field == "RoundingAdjustment").Value!, CultureInfo.InvariantCulture);
                // บรรทัดสรุปหักยอดหัก ณ ที่จ่ายไม่ได้ที่นี่ (ยอดรวมบนกระดาษไม่หัก) — ส่วนหัก ณ ที่จ่ายอยู่คนละช่อง
                Assert.True(Math.Abs(sum - decimal.Parse(totalText, CultureInfo.InvariantCulture)) <= OcrLineReconciler.Tolerance,
                    $"{p.Name}: Σ บรรทัด {sum} ≠ ยอดรวม {totalText}");
            }
        }
    }
}
