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
    public void ใบร้านวัสดุ_engineคืนแค่ยอดบรรทัด_ราคาที่เก็บเป็นยอดหลังลด_ส่วนลดศูนย์เปอร์เซ็นต์_ยอดVATรวมเท่าใบที่มีราคา()
    {
        // ทีม D 2026-10-13 (B5): กระดาษใบเดียวกับข้างบน แต่บรรทัดไม่มีจำนวน/ราคา ⇒ ยอดหลังกระจาย 351.50 · 845.50 · 128.25 (เท่าข้างบน)
        // VAT 24.61 · 59.19 · 8.97 (เท่าข้างบน) · ราคาที่เก็บ = ยอดหลังลด ⇒ ส่วนลด 0% / 0.00 บาท
        // (เดิม 5% / 0.00 ⇒ เปิดแก้แล้วบันทึก ComputeLineAmounts หัก 5% ซ้ำ: 351.50 → 333.92 — ล็อกด้วย OcrCaseCLineDiscountTests)
        Assert.Equal("Items", Val("hardware-bill-discount-amounts-only", "LineMode"));
        Assert.Equal("DiscountOnSubTotal", Val("hardware-bill-discount-amounts-only", "ReconCase"));
        Assert.Equal("false", Val("hardware-bill-discount-amounts-only", "PricesIncludeVat"));
        Assert.Equal("AllStandard7:7,7,7", Val("hardware-bill-discount-amounts-only", "LineVatPlan"));
        Assert.Equal(
            "351.50|7|24.61|351.50|0|0.00|0.00;845.50|7|59.19|845.50|0|0.00|0.00;128.25|7|8.97|128.25|0|0.00|0.00",
            Val("hardware-bill-discount-amounts-only", "Lines"));
        Assert.Equal("ok", Val("hardware-bill-discount-amounts-only", "IntegrityGaps"));
        Assert.Equal("0.00", Val("hardware-bill-discount-amounts-only", "RoundingAdjustment"));
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

    /// <summary>2026-10-16 ผู้ใช้รายงาน (Makro หน้า 2/2): เดิมได้บรรทัดสรุปเดียว 11,208.00 · 7% · VAT 638.50 (ยอดสุทธิ 11,846.50) แล้วคำแนะนำ
    /// "VAT/0.07" ของระบบพาไปแยกเป็น 9,121.43 + 2,086.57 · ตอนนี้ยึดยอด 10,980 (ยอดหัวสแกนคือ VAT บวกซ้ำ) แล้วสร้างบรรทัดตามตารางรหัส ภ.พ.:
    /// ยกเว้น 1,220.00 · มี VAT ฐาน 9,121.50 VAT 638.50 · Σ = 10,980.00 ตรงยอดที่ต้องชำระ/ตัวอักษร</summary>
    [Fact]
    public void ใบM2_Makro_หน้า2_ยกเว้น1220_มีVAT9121_50_รวม10980_ไม่ใช่11846_50()
    {
        Assert.Equal("VatGroups", Val("makro-page2-vat-added-twice", "LineMode"));
        Assert.Null(Val("makro-page2-vat-added-twice", "ReconCase"));
        Assert.Equal("1220.00|-1|0.00|1220.00|0|0.00|0.00;9121.50|7|638.50|9121.50|0|0.00|0.00",
            Val("makro-page2-vat-added-twice", "Lines"));
        Assert.Equal("ok", Val("makro-page2-vat-added-twice", "IntegrityGaps"));
        Assert.Equal("0.00", Val("makro-page2-vat-added-twice", "RoundingAdjustment"));
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

    // ── 2026-10-09: บิลค่าบริการรายเดือน (ค้างชำระจากรอบก่อน) — บรรทัดเดียว 1,000 / 7% / 70 ทั้งใบที่มีค้างและไม่มีค้าง ──

    [Fact]
    public void บิลเน็ต_บรรทัดค่าบริการ1000_VAT70_ทั้งใบที่มีค้างและไม่มีค้าง_ด่านΣไม่ฟ้องเพราะยึดใบกำกับรอบนี้แล้ว()
    {
        foreach (var paper in new[] { "telecom-prior-balance", "telecom-no-arrears" })
        {
            Assert.Equal("Items", Val(paper, "LineMode"));
            Assert.Equal("LinesMatchSubTotal", Val(paper, "ReconCase"));
            Assert.Equal("(ไม่ใช้)", Val(paper, "LineVatRates"));
            Assert.Equal("AllStandard7:7", Val(paper, "LineVatPlan"));
            Assert.Equal("1000.00|7|70.00|1000.00|0|0.00|0.00", Val(paper, "Lines"));
            // ก่อนแก้: ยอดรวมที่ยึด = 1,570 ⇒ บรรทัด 1,070 ≠ 1,570 ⇒ TotalMismatch — ตอนนี้ยึด 1,070 ⇒ ok
            Assert.Equal("ok", Val(paper, "IntegrityGaps"));
        }
    }

    // ── 2026-10-09 ผู้ใช้รายงาน: PDF ต้นฉบับ+สำเนา ⇒ engine คืน 5 บรรทัดสองรอบ ⇒ จำนวนเคย ×2 (ตัดสินที่ OcrDuplicateLineGuard) ──

    [Fact]
    public void ใบบุญทรัพย์_ต้นฉบับบวกสำเนา_สิบแถวจากengine_กลับมาห้าบรรทัดจำนวนตามกระดาษ_Σ21005_VAT1470_35_ไม่มีΣGAP()
    {
        // ก่อนแก้ (หน้าจอผู้ใช้): จำนวน 200/100/200/200/200 · Σ 42,010 = 2 × 21,005 ⇒ Ambiguous [Σ-GAP] · ตอนนี้ด่านแถวซ้ำตัด 5 แถวที่ถูกอ่านซ้ำ
        // (Σ ทุกแถว 42,010 ไม่ตรงหัวใบ · Σ หลังนับครั้งเดียว 21,005 ตรงยอดก่อน VAT) ⇒ เคส B · VAT 1,470.35 เฉลี่ยตามสัดส่วน:
        // 722.12 · 242.41 · 253.26 · 154.56 · เศษ 98.00 (Σ 1,470.35) · 7% × 21,005 = 1,470.35 พอดี ⇒ ทั้งใบ 7% · 10,316 + 1,470.35 … = 22,475.35 ✓
        Assert.Equal("5", Val("boonsap-original-copy-pages", "DupRowsDropped"));
        Assert.Equal("Items", Val("boonsap-original-copy-pages", "LineMode"));
        Assert.Equal("LinesMatchSubTotal", Val("boonsap-original-copy-pages", "ReconCase"));
        Assert.Equal("false", Val("boonsap-original-copy-pages", "PricesIncludeVat"));
        Assert.Equal("21005.00", Val("boonsap-original-copy-pages", "HeaderSubTotal"));
        Assert.Equal("22475.35", Val("boonsap-original-copy-pages", "HeaderTotal"));
        Assert.Equal("false", Val("boonsap-original-copy-pages", "HeaderSwapped"));
        Assert.Equal(
            "10316.00|7|722.12|103.16|0|0.00|0.00;3463.00|7|242.41|69.26|0|0.00|0.00;3618.00|7|253.26|36.18|0|0.00|0.00;"
            + "2208.00|7|154.56|22.08|0|0.00|0.00;1400.00|7|98.00|14.00|0|0.00|0.00",
            Val("boonsap-original-copy-pages", "Lines"));
        Assert.Equal("ok", Val("boonsap-original-copy-pages", "IntegrityGaps"));
        Assert.Equal("0.00", Val("boonsap-original-copy-pages", "RoundingAdjustment"));
    }

    [Fact]
    public void ใบน้ำดื่ม_กระดาษพิมพ์สองบรรทัดเหมือนกันจริง_หัวใบ100_ไม่ถูกตัด_สองบรรทัด50_VATบรรทัดละ3_50()
    {
        // Σ ทุกแถว 100 = ยอดก่อน VAT ⇒ กระดาษพิมพ์ซ้ำจริง ⇒ ด่านแถวซ้ำไม่แตะ (0) · เคส B · VAT 7 เฉลี่ย 3.50/3.50 · 50+3.5+50+3.5 = 107 ✓
        Assert.Equal("0", Val("water-genuine-repeat-rows", "DupRowsDropped"));
        Assert.Equal("LinesMatchSubTotal", Val("water-genuine-repeat-rows", "ReconCase"));
        Assert.Equal("50.00|7|3.50|5.00|0|0.00|0.00;50.00|7|3.50|5.00|0|0.00|0.00", Val("water-genuine-repeat-rows", "Lines"));
        Assert.Equal("ok", Val("water-genuine-repeat-rows", "IntegrityGaps"));
    }

    [Fact]
    public void ใบเดิมทุกใบในชุด_ด่านแถวซ้ำไม่ตัดอะไร_DupRowsDroppedเป็นศูนย์()
    {
        var all = OcrReplayHarness.Run();
        foreach (var p in OcrReplayHarness.Corpus.Where(p => p.EngineLines == null))
            Assert.Equal("0", all.Single(a => a.Paper == p.Name && a.Field == "DupRowsDropped").Value);
    }

    // ── ตัวเครื่องมือ: ทุกใบต้องมีแถวระดับบรรทัด · ไม่มีใบไหนได้ส่วนลดบาทติดลบ · ใบมีรายการ Σ ก่อน VAT + VAT = ยอดรวมเมื่อ ok ──

    // ── 2026-10-16 รหัส ภ.พ. รายบรรทัด (หน้า 1 สังเคราะห์ + หน้า 2 จริงของ Makro 405901117163) ─────────────────────────────

    [Fact]
    public void Makroหน้า1และ2_อัตราตามรหัสภพ_ไข่ไก่ยกเว้น_ส่วนลดแบ่งต่อรหัส_ยกเว้น1220_มีVAT9121_50()
    {
        // ก่อนแก้ (snapshot ก่อนต่อสาย): ไข่ไก่ 7% (เดาจากชื่อ) · ส่วนลด 2.03% ทุกบรรทัด ⇒ ยกเว้นได้ 979.66 · [Σ-GAP] VatRateMismatch
        // หลังแก้: รหัส 1 = ข้าวสาร + ไข่ไก่ (1,248 − ส่วนลดกลุ่ม 28 = 1,220.00 · 977.56 + 242.44) · รหัส 2 = 9,960 − 200 = 9,760 (ฐาน 9,121.50 · VAT 638.50)
        Assert.Equal("Items", Val("makro-p1p2-vatcode-synthetic", "LineMode"));
        Assert.Equal("DiscountOnTotalInclVat", Val("makro-p1p2-vatcode-synthetic", "ReconCase"));
        Assert.Equal("true", Val("makro-p1p2-vatcode-synthetic", "PricesIncludeVat"));
        Assert.Equal(
            "977.56|-1|0.00|1000.00|2.24|22.44|0.00;242.44|-1|0.00|248.00|2.24|5.56|0.00;"
            + "1038.53|7|72.70|189.00|2.01|22.77|0.00;2371.95|7|166.04|259.00|2.01|52.01|0.00;1080.66|7|75.65|59.00|2.01|23.69|0.00;"
            + "879.18|7|61.54|64.00|2.01|19.28|0.00;3751.18|7|262.57|512.00|2.01|82.25|0.00",
            Val("makro-p1p2-vatcode-synthetic", "Lines"));
        Assert.Equal("ok", Val("makro-p1p2-vatcode-synthetic", "IntegrityGaps"));
        Assert.Equal("0.00", Val("makro-p1p2-vatcode-synthetic", "RoundingAdjustment"));
    }

    [Fact]
    public void Makroหน้า2_Tesseractจริง_คำอธิบายรหัส1เพี้ยน_ค่าตั้งต้นยกเว้น_บรรทัดสรุปต่อรหัส_รวม10980()
    {
        // DECISIONS ข้อ 153 ข้อ 2 · cold start / ปิด AI (ชุดกระดาษไม่มีคำตอบ AI): รหัส 1 (VAT 0) ⇒ ยกเว้น · รหัส 2 ตัวเลขพิสูจน์ 7%
        // ก่อน: รหัส 1 ไม่รู้ ⇒ ตารางใช้ไม่ได้ ⇒ บรรทัดสรุปใบเดียว 10,341.50 · 7% · VAT 638.50 + VatRateMismatch
        Assert.Equal("VatGroups", Val("makro-page2-tesseract-garbled-legend", "LineMode"));
        Assert.Equal("1220.00|-1|0.00|1220.00|0|0.00|0.00;9121.50|7|638.50|9121.50|0|0.00|0.00",
            Val("makro-page2-tesseract-garbled-legend", "Lines"));
        Assert.Equal("ok", Val("makro-page2-tesseract-garbled-legend", "IntegrityGaps"));
    }

    [Fact]
    public void Makroหน้า1และ2ครบ_engineอ่านคอลัมน์รหัสไม่ได้_มีส่วนลด_รายการไม่ลงยอด_ตารางลงยอด_ตารางชนะ()
    {
        // คำตัดสินเจ้าของ 2026-10-16 ภาคต่อ ("ถ้าไม่ตรงและตารางภพ ตรงกับยอดรวม ให้ยึดตาราง ภพ") — เปลี่ยนโดยตั้งใจครั้งที่สามของใบนี้:
        // afbb0956/6b9bf926 คงรายการ 7 แถว + [Σ-GAP] (เลขหน้าครบ 1..2 ⇒ "คงรายการ") · ตอนนี้เลขหน้าไม่ใช่เกณฑ์: รายการแบ่งส่วนลดต่อรหัสไม่ได้
        // (ไข่ไก่ 7% จากชื่อ ⇒ ยกเว้น 979.66 ≠ 1,220) = ไม่ลงยอด · ตาราง "รวม" 10,980 = ยอดที่พิสูจน์ + VAT 638.50 ⇒ บรรทัดสรุปต่อรหัส (ไม่มี [Σ-GAP])
        Assert.Equal("VatGroupsInsteadOfItems", Val("makro-p1p2-no-row-codes-synthetic", "LineMode"));
        Assert.Null(Val("makro-p1p2-no-row-codes-synthetic", "ReconCase"));
        Assert.Equal("1220.00|-1|0.00|1220.00|0|0.00|0.00;9121.50|7|638.50|9121.50|0|0.00|0.00",
            Val("makro-p1p2-no-row-codes-synthetic", "Lines"));
        Assert.Equal("ok", Val("makro-p1p2-no-row-codes-synthetic", "IntegrityGaps"));
        Assert.Equal("false", Val("makro-p1p2-no-row-codes-synthetic", "PricesIncludeVat"));
    }

    [Theory]
    [InlineData("makro-page2-discount-rows-only", "52120|-")]
    [InlineData("makro-page2-discount-rows-mixed-accounts", "52120|-|mixed")]
    [InlineData("makro-page2-tesseract-discount-rows-only", "52120|-")]
    public void Makroหน้า2สแกนซ้ำ_engineคืนแถวส่วนลด3แถว_บรรทัดสรุปตามตาราง_ยกเว้น1220_มีVAT9121_50_ฐานหัวใบ10341_50(string paper, string dims)
    {
        // เจ้าของ 2026-10-16: "ยอดรวมลงถูกแต่บอดราย รายการ … เห็นตารางสรุปรวม ก็ใช้ตารางนั้นได้เลย ถ้ายอดรวมตรง" — ก่อนแก้ (ภาพหน้าจอ · ทำซ้ำผ่าน
        // PreviewDocumentLinesAsync จริง): แถวส่วนลด 30/60/10 เป็นสินค้า 7% + ⛔ "Σ บรรทัด 100.00 น้อยกว่ายอดก่อน VAT 10,341.50" · ยอดก่อน VAT หัวใบ 11,208
        Assert.Equal("VatGroupsInsteadOfItems", Val(paper, "LineMode"));
        Assert.Equal("1220.00|-1|0.00|1220.00|0|0.00|0.00;9121.50|7|638.50|9121.50|0|0.00|0.00", Val(paper, "Lines"));
        Assert.Equal("ok", Val(paper, "IntegrityGaps"));
        Assert.Equal(dims, Val(paper, "SummaryDims"));
        // แถวส่วนลดไม่ถูกหักซ้ำ: Σ บรรทัด + VAT = 10,980 = ยอดชำระ (ไม่ใช่ 10,880 · ไม่ใช่ 11,080)
        Assert.Equal("10980.00", Val(paper, "AnchorTotal"));
    }

    [Fact]
    public void Makroหน้า2สแกนซ้ำ_golden_หัวใบและระดับบรรทัดต่อใบ()
    {
        // golden ตัวอักษรต่อใบ (ocr_golden_corpus_check — ทุกใบต้องมี Val ของตัวเอง ทั้งหัวใบและระดับบรรทัด)
        Assert.Equal("Confirmed", Val("makro-page2-discount-rows-only", "AnchorVerdict"));
        Assert.Equal("228.00", Val("makro-page2-discount-rows-only", "BillDiscount"));
        Assert.Equal("VatGroupsInsteadOfItems", Val("makro-page2-discount-rows-only", "LineMode"));
        Assert.Equal("Confirmed", Val("makro-page2-discount-rows-mixed-accounts", "AnchorVerdict"));
        Assert.Equal("10341.50", Val("makro-page2-discount-rows-mixed-accounts", "PlannedHeaderSubTotal"));
        Assert.Equal("VatGroupsInsteadOfItems", Val("makro-page2-discount-rows-mixed-accounts", "LineMode"));
        Assert.Equal("52120|-|mixed", Val("makro-page2-discount-rows-mixed-accounts", "SummaryDims"));
        Assert.Equal("Unknown", Val("makro-page2-tesseract-discount-rows-only", "AnchorVerdict"));
        Assert.Equal("228.00", Val("makro-page2-tesseract-discount-rows-only", "BillDiscount"));
        Assert.Equal("VatGroupsInsteadOfItems", Val("makro-page2-tesseract-discount-rows-only", "LineMode"));
        Assert.Equal("ok", Val("makro-page2-tesseract-discount-rows-only", "IntegrityGaps"));
    }

    [Fact]
    public void Makroหน้า2สแกนซ้ำ_ยอดก่อนVATหัวใบ_ไม่ใช่ยอดรวมVATก่อนส่วนลด11208_แต่ฐานที่พิสูจน์10341_50()
    {
        // (c) OcrTotalAnchor.Plan สาขา Confirmed: ยอดรวม 10,980 ถูกแล้ว แต่ "ยอดก่อน VAT" 11,208 คือป้าย "ราคาสินค้ารวมภาษีมูลค่าเพิ่ม/ TOTAL" ⇒ ฐานที่พิมพ์ 10,341.50
        Assert.Equal("10341.50", Val("makro-page2-discount-rows-only", "PlannedHeaderSubTotal"));
        Assert.Equal("10341.50", Val("makro-page2-vat-added-twice", "PlannedHeaderSubTotal"));   // สาขา Proven เดิม (ไม่เปลี่ยน)
        // ทิศตรงข้าม: ใบที่ฐานถูกอยู่แล้ว / ไม่มีป้ายรวม VAT บนตัวเลขนั้น ⇒ ฐานเดิมทุกตัวอักษร
        Assert.Equal("951.00", Val("makro-correct", "PlannedHeaderSubTotal"));
        Assert.Equal("22663.97", Val("makro-page3-total-first", "PlannedHeaderSubTotal"));
        // ข้อความ Tesseract ยึดยอดไม่ได้ (Unknown) ⇒ ไม่แตะ (ไม่รู้ = ไม่เขียนทับ) — ช่องเปิดที่จดไว้ใน lessons
        Assert.Equal("11208.00", Val("makro-page2-tesseract-discount-rows-only", "PlannedHeaderSubTotal"));
    }

    [Fact]
    public void Makroหน้า1และ2_engineคืนแถวส่วนลดเป็นสินค้า_แปลงเป็นส่วนลด_ส่วนลดที่ระบุชื่อลงบรรทัดนั้น_ต่อกลุ่มตรงตาราง()
    {
        // คำตัดสินเจ้าของ 2026-10-16 ข้อ 3: แถวส่วนลด 5 แถว (28 · 100 · 30 · 60 · 10 = รวมส่วนลด 228) ไม่มีรหัส ภ.พ. ⇒ ส่วนลดของเอกสาร (ห้ามพองยอดรวม)
        // "ลดราคาไข่ไก่" เอ่ยชื่อสินค้า ⇒ ไข่ไก่ 248 − 28 = 220 (ข้าวสารไม่ถูกหัก) · "สินค้าฮีโร่" ⇒ ถุงขยะดำ ฮีโร่ −30 + ส่วนแบ่งของ 170 ที่เหลือในกลุ่ม 7%
        // ก่อน: Σ-GAP:Ambiguous,Overstated · แถวส่วนลดเป็นสินค้า 7% ยอดบวก (ยอดเอกสารเกินกระดาษ 456)
        Assert.Equal("DiscountOnTotalInclVat", Val("makro-p1p2-discount-rows-as-items-synthetic", "ReconCase"));
        Assert.Equal("ok", Val("makro-p1p2-discount-rows-as-items-synthetic", "IntegrityGaps"));
        var rows = Val("makro-p1p2-discount-rows-as-items-synthetic", "Lines")!.Split(';');
        Assert.Equal(7, rows.Length);
        Assert.Equal("1000.00|-1|0.00|1000.00|0|0.00|0.00", rows[0]);
        Assert.Equal("220.00|-1|0.00|248.00|11.29|28.00|0.00", rows[1]);
        decimal Col(string r, int c) => decimal.Parse(r.Split('|')[c], CultureInfo.InvariantCulture);
        Assert.Equal(1220.00m, rows.Take(2).Sum(r => Col(r, 0)));                       // ยกเว้นตามตาราง
        Assert.Equal(9121.50m, rows.Skip(2).Sum(r => Col(r, 0)));                       // ฐานรหัส 2 ตามตาราง
        Assert.Equal(638.50m, rows.Skip(2).Sum(r => Col(r, 2)));                        // VAT ตามตาราง
        Assert.Equal(228.00m, rows.Sum(r => Col(r, 5)));                                // ส่วนลดบาทรวม = ส่วนลดบนกระดาษ
        Assert.Equal(10980.00m, rows.Sum(r => Col(r, 0) + Col(r, 2)));                  // = ยอดชำระบนกระดาษ
    }

    // ── 2026-10-16 ตัวกันจำนวนระเบิดถามแถวบนกระดาษ (OcrLineQuantityGuard · ใบจริง RE-20261000111 + ทิศตรงข้าม 3 ใบ) ─────────────────

    [Fact]
    public void ใบจริงRE111_พรามี่_จำนวน1_ราคา1330รวมVAT_ส่วนลดบรรทัด293_ฐาน969_16_VAT67_84_ไม่หารจำนวน()
    {
        // กระดาษ: 1.00 ชิ้น × 1,330.00 − ส่วนลด 293.00 = 1,037.00 (มูลค่ารวมภาษี) · ฐาน 7% 969.16 · VAT 67.84 · รวม 1,037.00
        // คิดมือ: 1,037 × 100/107 = 969.158… ⇒ 969.16 · VAT = 1,037 − 969.16 = 67.84 (= กระดาษ) · ส่วนลด 293 อยู่ฐานรวม VAT (ฐานเดียวกับราคา)
        // ก่อนแก้ (ใบสำคัญจ่ายของผู้ใช้): จำนวน 0.78 (= 1,037 ÷ 1,330) · ส่วนลด 0% — ยอดรวมตรงจึงไม่มีอะไรฟ้อง
        const string P = "pramie-line-discount-re111";
        Assert.Equal("PrintedDiscount|1|1330.00|293.00|note", Val(P, "LineQtyGuard"));   // ฝ่ายค้าน 658e3705 P1-1 ข้อ d: หมายเหตุแจ้ง (หัวคอลัมน์ "ส่วนลด") ไม่บล็อก
        Assert.Equal("Items", Val(P, "LineMode"));
        Assert.Equal("PricesIncludeVat", Val(P, "ReconCase"));
        Assert.Equal("true", Val(P, "PricesIncludeVat"));
        Assert.Equal("969.16|7|67.84|1330.00|0|293.00|0.00", Val(P, "Lines"));
        Assert.Equal("ok", Val(P, "IntegrityGaps"));
        Assert.Equal("0.00", Val(P, "RoundingAdjustment"));
        // ส่วนลดรายบรรทัดต้องไม่ถูกนับซ้ำเป็นส่วนลดท้ายบิล (ข้อความ PDF มีคำ "ส่วนลด" เป็นหัวคอลัมน์)
        Assert.Null(Val(P, "BillDiscount"));
        Assert.Equal("0.00", Val(P, "DiscountToSpread"));
    }

    [Fact]
    public void ทิศตรงข้าม_สินค้าชั่งน้ำหนัก_จำนวนไม่ได้พิมพ์_ยอดหารราคา0_45กก_ยังทำ()
    {
        Assert.Equal("FixQuantity|0.45|120.00|-|block", Val("weighed-item-qty-not-printed", "LineQtyGuard"));   // 0.45 ไม่ได้พิมพ์ ⇒ บล็อก
        Assert.Equal("54.00|0|0.00|120.00|0|0.00|0.00", Val("weighed-item-qty-not-printed", "Lines"));
        Assert.Equal("ok", Val("weighed-item-qty-not-printed", "IntegrityGaps"));
    }

    [Fact]
    public void ทิศตรงข้าม_engineอ่านจำนวน1_กระดาษพิมพ์2_จำนวนจากยอดหารราคาเป็น2_พิสูจน์แล้วไม่บล็อก()
    {
        // ฝ่ายค้าน 658e3705 P2-3: กระดาษพิมพ์ 2 ติดก่อนราคา = จำนวนใหม่พิสูจน์แล้ว ⇒ หมายเหตุแจ้ง (เดิมบล็อก)
        Assert.Equal("FixQuantity|2|10.00|-|note", Val("qty-misread-engine-one", "LineQtyGuard"));
        Assert.Equal("20.00|7|1.40|10.00|0|0.00|0.00", Val("qty-misread-engine-one", "Lines"));
        Assert.Equal("ok", Val("qty-misread-engine-one", "IntegrityGaps"));
    }

    [Fact]
    public void จำนวนพิมพ์บนแถว_ส่วนต่างไม่มีส่วนลดอธิบาย_คงจำนวน2_ไม่แต่ง1_5_ส่วนต่างเป็นส่วนลดที่ระบุชัด_บรรทัดลงตัว_บล็อก()
    {
        // ฝ่ายค้าน 658e3705 P2-5: เดิมเก็บ 2 × 100 − 0 ≠ 150 (บันทึกซ้ำได้ 200) + Σ-GAP · ตอนนี้ 2 × 100 − 50 = 150 (ยอดตรงกระดาษ · บันทึกซ้ำไม่เลื่อน)
        // ส่วนต่าง 50 ไม่มีบนกระดาษ ⇒ แท็กบล็อก [LINE-QTY] "ส่วนต่างที่กระดาษไม่บอก — ตรวจ" (Σ ตรงหัวใบแล้วจึงไม่มี Σ-GAP)
        Assert.Equal("KeepPrintedGap|2|100.00|50.00|block", Val("qty-printed-gap-unexplained", "LineQtyGuard"));
        Assert.Equal("150.00|7|10.50|100.00|0|50.00|0.00", Val("qty-printed-gap-unexplained", "Lines"));
        Assert.Equal("ok", Val("qty-printed-gap-unexplained", "IntegrityGaps"));
    }

    // ── ฝ่ายค้าน 658e3705 (ทีม OCR 6) — ใบละหนึ่ง defect class ─────────────────────────────────────────────

    [Fact]
    public void ฝ่ายค้าน_คอลัมน์หักณที่จ่าย_ยอดบรรทัดก่อนหัก10000_หักณที่จ่าย300ไปที่ตัวอ่านWHT_ไม่ใช่ส่วนลด()
    {
        // เดิม: PrintedDiscount 300 (ส่วนลดบรรทัด) · อนุมัติเองได้ · ฐาน VAT 9,700 ≠ กระดาษ 10,000
        const string P = "wht-column-service-invoice";
        Assert.Equal("DeductionRow|1|10000.00|-|block", Val(P, "LineQtyGuard"));
        Assert.Equal("10000.00|7|700.00|10000.00|0|0.00|300.00", Val(P, "Lines"));   // หัก ณ ที่จ่าย 300 = อัตรา 3% ของเจ้าของเดิม (ไม่ใช่ส่วนลด)
        Assert.Equal("ok", Val(P, "IntegrityGaps"));
        // ⚠️ เจอแต่ไม่แก้ (เจ้าของคนละตัว · รอแยกรอบ): ป้าย "หัก ณ ที่จ่าย" บนหัวคอลัมน์ทำให้ PaperWhtReader หยิบ 10,000.00 (เงินแรกหลังป้าย) แทน 300.00
        // ⇒ ขั้นยึดยอดได้ Conflict (แท็กบล็อก [TOTAL-CONFLICT]) — ค่าปัจจุบันล็อกไว้ให้การแก้รอบหน้าเห็นเป็นตัวเลข
        Assert.Equal("10000.00", Val(P, "PaperWhtAmount"));
        Assert.Equal("Conflict", Val(P, "AnchorVerdict"));
    }

    [Fact]
    public void ฝ่ายค้าน_ใบงวดหักมัดจำ_ยอดบรรทัด100000_ไม่ใช่ส่วนลด30000_ฟ้องΣGAPให้ตัดมัดจำผ่านวงจรมัดจำ()
    {
        // เดิม: ส่วนลดบรรทัด 30,000 ⇒ ลดราคาถาวร มัดจำ 11810 ไม่ถูกล้าง · ตอนนี้ยอดก่อนหัก + [LINE-QTY] + [Σ-GAP] (ไม่แต่งเป็นส่วนลดท้ายบิล)
        const string P = "deposit-installment-invoice";
        Assert.Equal("DeductionRow|1|100000.00|-|block", Val(P, "LineQtyGuard"));
        Assert.Equal("Ambiguous", Val(P, "ReconCase"));
        Assert.Equal("0.00", Val(P, "DiscountToSpread"));
        Assert.Equal("Σ-GAP:Ambiguous", Val(P, "IntegrityGaps"));
        // ⚠️ เจอแต่ไม่แก้: OcrDepositMarker ตีใบงวดที่ "หักมัดจำ" กลางแถว (แถวไม่ขึ้นต้นด้วย หัก) เป็นใบมัดจำ — ล็อกค่าปัจจุบัน (รอแยกรอบ)
        Assert.Equal("true", Val(P, "IsDeposit"));
    }

    [Fact]
    public void ฝ่ายค้าน_รหัสสินค้าSKU50_ไม่ใช่ส่วนลดจากกระดาษ_ยอดตรงกระดาษ_บล็อก()
    {
        const string P = "sku-code-on-row";
        Assert.Equal("UnevidencedDiscount|1|500.00|50.00|block", Val(P, "LineQtyGuard"));
        Assert.Equal("450.00|0|0.00|500.00|0|50.00|0.00", Val(P, "Lines"));
        Assert.Equal("ok", Val(P, "IntegrityGaps"));
    }

    [Fact]
    public void ฝ่ายค้าน_เลขลำดับแถวไม่ใช่จำนวน_หมูสับ0_45กก_บล็อก_แถวที่ลงตัวไม่แตะ()
    {
        const string P = "row-index-receipt";
        Assert.Equal("FixQuantity|0.45|120.00|-|block;Keep|10|4.00|-|-", Val(P, "LineQtyGuard"));
        Assert.Equal("54.00|0|0.00|120.00|0|0.00|0.00;40.00|0|0.00|4.00|0|0.00|0.00", Val(P, "Lines"));
        Assert.Equal("ok", Val(P, "IntegrityGaps"));
    }

    [Fact]
    public void ฝ่ายค้าน_บิลค่าไฟมิเตอร์พิมพ์คู่หน่วย_ลง1คูณยอด_จำนวนอยู่ในคำอธิบาย_แจ้งไม่บล็อก()
    {
        // เดิม (658e3705): [LINE-QTY] บล็อกทุกเดือน · มิเตอร์ 3,611 "หน่วย" แล้วยอด 16,351.48 ตามมา = พิสูจน์แล้ว
        // ★ ทีม OCR 8 (2026-10-16): เดิม "FixUnitPrice|3611|4.5282|-|note" + ราคาต่อหน่วยในบรรทัด 4.53 ⇒ บันทึกซ้ำ 3,611 × 4.53 = 16,357.83 ≠ 16,351.48 ·
        // ตอนนี้ 1 × 16,351.48 · ราคาต่อหน่วย = ยอด · "(3,611 หน่วย)" ในคำอธิบาย · ยอด/VAT/รวม เท่าเดิมทุกสตางค์
        const string P = "utility-meter-bill";
        Assert.Equal("QuantityInDescription|1|16351.48|-|note", Val(P, "LineQtyGuard"));
        Assert.Equal("ค่าไฟฟ้า (3,611 หน่วย)", Val(P, "LineDescriptions"));
        Assert.Equal("16351.48|7|1144.60|16351.48|0|0.00|0.00", Val(P, "Lines"));
        Assert.Equal("ok", Val(P, "IntegrityGaps"));
        Assert.Equal("0.00", Val(P, "RoundingAdjustment"));
    }

    [Fact]
    public void บิลค่าไฟจริง_กฟภ_ไม่มีราคาต่อหน่วยบนกระดาษ_ลง1คูณยอด_4272หน่วยในคำอธิบาย_ยอดVATรวมตรงกระดาษ()
    {
        // ทีม OCR 8 (2026-10-16 · PDF จริง XH0712609004157): ตาราง จำนวนหน่วย 4,272.00 · จำนวนเงิน 19,381.50 — ไม่มีคอลัมน์ราคาต่อหน่วย ·
        // ตัวเลข 4 ตำแหน่งตัวเดียวบนกระดาษคือ อัตราค่า Ft 0.1623 · ก่อนแก้: ราคา 19,381.50 ÷ 4,272 = 4.5369 ⇒ เก็บ 4.54 ⇒ บันทึกซ้ำ 19,394.88 (+13.38)
        const string P = "pea-electricity-bill-0869";
        Assert.Equal("19381.50", Val(P, "HeaderSubTotal"));
        Assert.Equal("20738.21", Val(P, "HeaderTotal"));
        Assert.Equal("20738.21", Val(P, "AnchorTotal"));
        // ⚠️ ตัวยึดยอดรวมพิสูจน์จากกระดาษไม่ได้ (ข้อความ PdfPig พิมพ์ตัวเลขก่อนป้าย "รวมทั้งสิ้น (Total)") ⇒ ใช้ยอดของ engine — ล็อกคำตอบวันนี้
        Assert.Equal("Unknown", Val(P, "AnchorVerdict"));
        Assert.Equal("19381.50", Val(P, "AnchoredNetSubTotal"));
        Assert.Equal("false", Val(P, "IsDeposit"));
        Assert.Null(Val(P, "PaperWhtAmount"));
        Assert.Equal("Items", Val(P, "LineMode"));
        Assert.Equal("LinesMatchSubTotal", Val(P, "ReconCase"));
        Assert.Equal("false", Val(P, "PricesIncludeVat"));
        // จำนวนหน่วยพิมพ์หลังป้าย "หน่วยที่ใช้ 4272.00" แล้วยอด 19,381.50 ตามมา = พิสูจน์แล้ว ⇒ แจ้ง ไม่บล็อก
        Assert.Equal("QuantityInDescription|1|19381.50|-|note", Val(P, "LineQtyGuard"));
        Assert.Equal("ค่าไฟฟ้า ประจำเดือน 08/2569 (4,272 หน่วย)", Val(P, "LineDescriptions"));
        // ยอดก่อนVAT|อัตรา|VAT|ราคาต่อหน่วย|ส่วนลด%|ส่วนลดบาท|หัก ณ ที่จ่าย — 19,381.50 + 1,356.71 = 20,738.21 ✓
        Assert.Equal("19381.50|7|1356.71|19381.50|0|0.00|0.00", Val(P, "Lines"));
        Assert.Equal("ok", Val(P, "IntegrityGaps"));
        Assert.Equal("0.00", Val(P, "RoundingAdjustment"));
        Assert.Equal("0", Val(P, "DupRowsDropped"));
    }

    // ── ทีม OCR 10 (2026-10-16): ใบจริงสองใบเดิมด้วยข้อความ PDF ที่ประกอบตามพิกัด — ตัวเลข engine/บรรทัดเท่าใบเดิม ⇒ ผลต่างทั้งหมดมาจากข้อความ ──

    [Fact]
    public void บิลกปภ_WE1105_รอบบิลล้วน_ลงหนึ่งคูณยอด_ก่อนVAT2839_VAT198_73()
    {
        // ผู้ใช้รายงาน 2026-10-19: engine ให้ "10/2569" × 101,000 ราคา 0.01 (Σ 1,010 ≠ 2,839) — "อย่างน้อยควรเป็น จำนวน 1 และ ราคา 2839 และ + vat 7%"
        const string P = "pwa-water-bill-we1105-positional";
        Assert.Equal("QuantityInDescription|1|2839.00|-|note", Val(P, "LineQtyGuard"));
        Assert.Equal("ค่าน้ำประปา ประจำเดือน 10/2569 (จำนวน 101,000)", Val(P, "LineDescriptions"));
        // ยอดก่อนVAT|อัตรา|VAT|ราคาต่อหน่วย|ส่วนลด%|ส่วนลดบาท|หัก ณ ที่จ่าย — 2,839.00 + 198.73 = 3,037.73 ✓
        Assert.Equal("2839.00|7|198.73|2839.00|0|0.00|0.00", Val(P, "Lines"));
        Assert.Equal("ok", Val(P, "IntegrityGaps"));
    }

    [Fact]
    public void ทีมOCR10_บิลกฟภ_ข้อความตามพิกัด_ตัวยึดยอดพิสูจน์ได้_บรรทัดเท่าใบเดิมทุกช่อง()
    {
        const string P = "pea-electricity-bill-0869-positional";
        const string Old = "pea-electricity-bill-0869";
        // เปลี่ยน (ตั้งใจ): ป้าย "รวมทั้งสิ้น (Total)" นำหน้า 20,738.21 แล้ว ⇒ พิสูจน์ยอดรวมจากกระดาษได้ (ใบเดิม Unknown — ตัวเลขพิมพ์ก่อนป้าย)
        Assert.Equal("Unknown", Val(Old, "AnchorVerdict"));
        Assert.Equal("Confirmed", Val(P, "AnchorVerdict"));
        Assert.Equal("20738.21", Val(P, "AnchorTotal"));
        // เปลี่ยน (ตั้งใจ): VAT 1,356.71 อยู่คู่ป้าย "ภาษีมูลค่าเพิ่ม (VAT)7 %" ⇒ ชั้นตัวเลขหัวใบพิสูจน์อัตราทั้งใบได้ (ใบเดิม Unknown)
        Assert.Equal("Unknown", Val(Old, "LineVatPlan"));
        Assert.Equal("AllStandard7:7", Val(P, "LineVatPlan"));
        // ไม่เปลี่ยน: บรรทัด 1 × 19,381.50 + "(4,272 หน่วย)" · ยอด/VAT/รวม · หัก ณ ที่จ่ายว่าง · ไม่มีส่วนลด
        foreach (var f in new[] { "LineQtyGuard", "LineDescriptions", "Lines", "IntegrityGaps", "RoundingAdjustment", "HeaderSubTotal", "HeaderTotal",
                     "AnchoredNetSubTotal", "PaperWhtAmount", "BillDiscount", "IsDeposit", "ReconCase", "PricesIncludeVat" })
            Assert.Equal(Val(Old, f), Val(P, f));
        Assert.Equal("QuantityInDescription|1|19381.50|-|note", Val(P, "LineQtyGuard"));
        Assert.Equal("19381.50|7|1356.71|19381.50|0|0.00|0.00", Val(P, "Lines"));
    }

    [Fact]
    public void ทีมOCR10_ใบพรามี่_ข้อความตามพิกัด_ส่วนลดบรรทัด293ตามเดิม_หักณที่จ่าย0บาทไม่ถูกอ่านเป็นยอดถัดไป()
    {
        const string P = "pramie-re111-positional";
        const string Old = "pramie-line-discount-re111";
        // ไม่เปลี่ยน: ตัวกันจำนวนอ่านส่วนลด 293.00 จากแถวที่แยกช่องแล้ว (เดิมอ่านจากแถวที่ติดกัน) · ฐาน 969.16 · VAT 67.84
        foreach (var f in new[] { "LineQtyGuard", "Lines", "IntegrityGaps", "RoundingAdjustment", "ReconCase", "PricesIncludeVat", "AnchorVerdict",
                     "AnchorTotal", "BillDiscount", "DiscountToSpread", "IsDeposit" })
            Assert.Equal(Val(Old, f), Val(P, f));
        Assert.Equal("PrintedDiscount|1|1330.00|293.00|note", Val(P, "LineQtyGuard"));
        Assert.Equal("969.16|7|67.84|1330.00|0|293.00|0.00", Val(P, "Lines"));
        // ทิศที่ห้ามแย่ลง: ป้าย "จำนวนเงินที่ถูกหัก ณ ที่จ่าย 0.00 บาท" อ่านได้แล้ว (PUA แปลงแล้ว) — ห้ามหยิบ 1,037.00 ของบรรทัดถัดไปเป็นยอดหัก
        Assert.Null(Val(P, "PaperWhtAmount"));
        Assert.Null(Val(P, "PaperWhtRate"));
        // เปลี่ยน (ตั้งใจ): VAT 67.84 อยู่คู่ป้าย ⇒ ชั้นตัวเลขหัวใบพิสูจน์อัตราได้
        Assert.Equal("Unknown", Val(Old, "LineVatPlan"));
        Assert.Equal("AllStandard7:7", Val(P, "LineVatPlan"));
    }

    [Fact]
    public void ทีมOCR9_บรรทัดสินค้าที่engineหลุดคอลัมน์ราคา_คงจำนวน_ไม่กลายเป็น1คูณยอด()
    {
        // ฝ่ายค้าน 13a4d788 P0: ทีม 8 ลงทั้งสามใบเป็น "QuantityInDescription|1|<ยอด>|-|note" + "(12 ขวด)"/"(30.512 ลิตร)"/"(5 กก.)" ในคำอธิบาย ⇒
        // นำเข้าสต็อก 1 หน่วย (ขาด 11 / 29.512 / 4) · ทุนเฉลี่ยเพี้ยน · ตัดยอด PO ได้ 1 — ไม่มีอะไรบล็อก
        // (ก) 300 ÷ 12 = 25.00 ลงตัวทุกสตางค์ ⇒ 12 × 25.00 (รุ่น 5 เดิม) · หมายเหตุแจ้ง · คำอธิบายไม่ถูกต่อ (ไม่มีแถว LineDescriptions)
        Assert.Equal("FillUnitPrice|12|25.00|-|note", Val("goods-qty-price-column-lost", "LineQtyGuard"));
        Assert.Equal("300.00|0|0.00|25.00|0|0.00|0.00", Val("goods-qty-price-column-lost", "Lines"));
        Assert.DoesNotContain(OcrReplayHarness.Run(), a => a.Paper.StartsWith("goods-", StringComparison.Ordinal) && a.Field == "LineDescriptions");
        // (ข) 990.09 ÷ 30.512 = 32.4492 ⇒ 32.45 · 30.512 × 32.45 = 990.11 ⇒ ส่วนลดบรรทัด 0.02 (ยอดตรงกระดาษ) · แท็กบล็อก (ส่วนต่าง 2 สตางค์)
        Assert.Equal("RoundedUnitPrice|30.512|32.45|0.02|block", Val("goods-fractional-qty-no-price", "LineQtyGuard"));
        Assert.Equal("990.09|0|0.00|32.45|0|0.02|0.00", Val("goods-fractional-qty-no-price", "Lines"));
        Assert.Equal("0.00", Val("goods-fractional-qty-no-price", "RoundingAdjustment"));
        // (ค) engine อ่านยอดมาใส่ช่องราคา (เคส ก) · "5 กก." พิมพ์คู่หน่วยนับ ⇒ จำนวนพิสูจน์ · 450 ÷ 5 = 90.00 ⇒ 5 × 90 (แจ้ง)
        Assert.Equal("FixUnitPrice|5|90.00|-|note", Val("goods-price-read-as-amount", "LineQtyGuard"));
        Assert.Equal("450.00|0|0.00|90.00|0|0.00|0.00", Val("goods-price-read-as-amount", "Lines"));
        foreach (var p in new[] { "goods-qty-price-column-lost", "goods-fractional-qty-no-price", "goods-price-read-as-amount" })
            Assert.Equal("ok", Val(p, "IntegrityGaps"));
    }

    [Fact]
    public void ฝ่ายค้าน_ทิศตรงข้าม_หัวคอลัมน์ส่วนลด_3เปอร์เซ็นต์เป็นส่วนลดจริง_แจ้งไม่บล็อก()
    {
        const string P = "discount-header-invoice";
        Assert.Equal("PrintedDiscount|1|10000.00|300.00|note", Val(P, "LineQtyGuard"));
        Assert.Equal("9700.00|7|679.00|10000.00|0|300.00|0.00", Val(P, "Lines"));
        Assert.Equal("ok", Val(P, "IntegrityGaps"));
    }

    [Fact]
    public void ฝ่ายค้าน_อัตราแท็กของชุด_บล็อกเฉพาะใบที่ไม่มีหลักฐาน()
    {
        // อัตราแท็กมีความหมาย: ใบเก่า 0 แท็ก · ใบที่พิสูจน์ได้ (ส่วนลดมีหัว · จำนวนพิมพ์ · มิเตอร์) = แจ้ง · ใบที่ไม่มีหลักฐาน = บล็อก
        var rows = OcrReplayHarness.Run().Where(a => a.Field == "LineQtyGuard").SelectMany(a => a.Value!.Split(';').Select(r => (a.Paper, Tag: r.Split('|')[4])));
        var block = rows.Where(r => r.Tag == "block").Select(r => r.Paper).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var note = rows.Where(r => r.Tag == "note").Select(r => r.Paper).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "deposit-installment-invoice", "goods-fractional-qty-no-price", "qty-printed-gap-unexplained", "row-index-receipt", "sku-code-on-row", "weighed-item-qty-not-printed", "wht-column-service-invoice" }, block);
        Assert.Equal(new[] { "discount-header-invoice", "goods-price-read-as-amount", "goods-qty-price-column-lost", "pea-electricity-bill-0869",
            "pea-electricity-bill-0869-positional", "pramie-line-discount-re111", "pramie-re111-positional", "pwa-water-bill-we1105-positional", "qty-misread-engine-one", "utility-meter-bill" }, note);
    }

    [Fact]
    public void ใบเดิมทุกใบในชุด_ตัวกันจำนวนระเบิดไม่แตะบรรทัดใดเลย()
    {
        // ratchet ของรอบนี้: ใบที่มีอยู่ก่อน 2026-10-16 ทุกบรรทัดได้ Keep (จำนวน/ราคาตามที่ป้อน · ไม่มีส่วนลดใหม่) — ใบใหม่ 4 ใบข้างบนเท่านั้นที่เปลี่ยน
        var added = new[] { "pramie-line-discount-re111", "weighed-item-qty-not-printed", "qty-misread-engine-one", "qty-printed-gap-unexplained",
            // ฝ่ายค้าน 658e3705 (ทีม OCR 6)
            "wht-column-service-invoice", "deposit-installment-invoice", "sku-code-on-row", "row-index-receipt", "utility-meter-bill", "discount-header-invoice",
            // ทีม OCR 8 (บิลค่าไฟจริง กฟภ.)
            "pea-electricity-bill-0869",
            // ทีม OCR 9 (ฝ่ายค้าน 13a4d788 P0 — บรรทัดสินค้าห้ามกลายเป็น 1 × ยอด)
            "goods-qty-price-column-lost", "goods-fractional-qty-no-price", "goods-price-read-as-amount",
            // ทีม OCR 10 (สองใบจริงข้างบนด้วยข้อความ PDF ตามพิกัด)
            "pea-electricity-bill-0869-positional", "pramie-re111-positional",
            // 2026-10-19 บิล กปภ. (รอบบิลล้วนบนบิลสาธารณูปโภค = มิเตอร์)
            "pwa-water-bill-we1105-positional" };
        var all = OcrReplayHarness.Run();
        foreach (var a in all.Where(a => a.Field == "LineQtyGuard" && !added.Contains(a.Paper)))
            foreach (var row in a.Value!.Split(';'))
                Assert.StartsWith("Keep|", row);
    }

    [Fact]
    public void R34_ใบRapbizz_SPIN00933_เส้นกระดาษ_เคสF_ส่วนลดท้ายบิล538_32ตามกระดาษไม่กระจาย_บรรทัด1226_84และ113_35_VAT85_88และ7_93()
    {
        // คำตัดสินข้อ 159 บนเส้นกระดาษของใบจริง: Σ บรรทัด 2,010 (รวม VAT) · ฐาน 1,340.19 · VAT 93.81 · รวม 1,434 · ส่วนลดบนกระดาษ 538.32 (ก่อน VAT) ⇒ เคส F
        // ⇒ Mode "Items+Bill:538.32" (BillDiscountAmount ตามกระดาษ · ไม่กระจาย) · บรรทัด = DocumentService.PlanLineAmounts: ฐานก่อนลด 1,719.63 + 158.88 = 1,878.51
        // (= "จำนวนเงิน Amount") − จัดสรร 492.79/45.53 ⇒ 1,226.84/113.35 · VAT 85.88/7.93 (ตัวเลขชุดเดียวกับเส้น e-Tax ของใบนี้ — OcrRapbizzSpIn00933Tests.ทีมT_เส้นจริงทั้งสาย_*)
        // ราคาต่อหน่วยตามกระดาษ 920/170 · ส่วนลดบรรทัด 0 · RA 0 (Σ ฐานบรรทัด 1,340.19 = ฐานกระดาษ) · ไม่มี [Σ-GAP]
        // ⚠️ ตัวเลขคิดมือจากกระดาษ + สูตรที่เทสต์ e-Tax ของใบเดียวกันล็อกไว้ (ไม่มี SDK ในเครื่อง 2026-10-26) — แถวที่ล้มใน CI = สาขา F ของ harness ต่างจาก service ต้องอธิบาย ไม่ใช่แก้เลข
        const string P = "rapbizz-spin00933-prevat-discount-on-incl";
        Assert.Equal("1340.19", Val("rapbizz-spin00933-prevat-discount-on-incl", "HeaderSubTotal"));   // literal ชื่อใบให้ tools/ocr_golden_corpus_check.py เห็น
        Assert.Equal("1434.00", Val(P, "HeaderTotal"));
        Assert.Equal("false", Val(P, "HeaderSwapped"));
        Assert.Equal("538.32", Val(P, "BillDiscount"));
        Assert.Equal("538.32", Val(P, "DiscountToSpread"));
        Assert.Equal("1340.19", Val(P, "NetSubTotal"));
        Assert.Equal("Items+Bill:538.32", Val(P, "LineMode"));
        Assert.Equal("DiscountPreVatOnInclusivePrices", Val(P, "ReconCase"));
        Assert.Equal("true", Val(P, "PricesIncludeVat"));
        Assert.Equal("1226.84|7|85.88|920.00|0|0.00|0.00;113.35|7|7.93|170.00|0|0.00|0.00", Val(P, "Lines"));
        Assert.Equal("ok", Val(P, "IntegrityGaps"));
        Assert.Equal("0.00", Val(P, "RoundingAdjustment"));
        Assert.Equal("0", Val(P, "DupRowsDropped"));   // ชุดนี้ป้อนแถวที่พิมพ์ 2 แถว (ไม่ใช่แถว engine ต้นฉบับ+สำเนา) ⇒ ไม่มีอะไรให้ตัด
        // ทิศตรงข้าม (หลัก ข): ใบที่กระจายส่วนลดได้ตรง ๆ (เคส C ร้านวัสดุ · เคส E ซูเปอร์) ยังเป็น Mode "Items" ไม่มี Bill:
        Assert.Equal("Items", Val("hardware-bill-discount", "LineMode"));
        Assert.Equal("Items", Val("supermarket-member-discount", "LineMode"));
        Assert.DoesNotContain(OcrReplayHarness.Run(), a => a.Field == "LineMode" && a.Paper != P && a.Value!.StartsWith("Items+Bill:", StringComparison.Ordinal));
    }

    [Fact]
    public void ทุกใบในชุด_มีแถวระดับบรรทัดครบ_และไม่มีใบไหนที่ok_แล้วยอดบรรทัดไม่ปิดยอดรวม()
    {
        var all = OcrReplayHarness.Run();
        foreach (var p in OcrReplayHarness.Corpus)
        {
            var mode = all.Single(a => a.Paper == p.Name && a.Field == "LineMode").Value;
            Assert.Contains(mode, new[] { "Items", "VatGroups", "VatGroupsInsteadOfItems", "Summary" });
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
