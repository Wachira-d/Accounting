using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Ocr;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **บรรทัดสินค้าห้ามกลายเป็น 1 × ยอดเงียบ ๆ — 1 × ยอด เฉพาะบรรทัดที่มีหลักฐานมิเตอร์** (ทีม OCR 9 · ฝ่ายค้าน 13a4d788 · 2026-10-16)
///
/// <para>ที่มา (P0): 13a4d788 (ทีม 8 · บิลค่าไฟ กฟภ.) ลงทุกบรรทัดที่ราคาว่างและราคาไม่พิมพ์บนแถวเป็น 1 × ยอด + "(12 ขวด)" ในคำอธิบาย ด้วยหมายเหตุ
/// <b>ไม่บล็อก</b> ⇒ น้ำดื่ม 12 / 300 ⇒ 1 × 300 · พรีวิวนำเข้าสต็อกอ่าน <c>detectedQty = line.Quantity</c> = 1 ⇒ CurrentStock ขาด 11 · ทุนเฉลี่ย
/// ถ่วงน้ำหนักเพี้ยน 12 เท่า · ตัดยอด PO ผ่าน SourceLineId ได้ 1 — ทั้งที่รุ่น 5 ได้ 12 × 25.00 ที่บันทึกซ้ำได้ตรง (ถดถอยล้วน) · AI แตกบรรทัด/ระบบภายนอก
/// ปล่อยราคาว่างโดยตั้งใจ ⇒ ทุกบรรทัดจำนวน &gt; 1 ของสองทางนั้นเข้าแขนงนี้ · (P1) หลักฐาน "มิเตอร์" เดิมคือคำหน่วยนับใดก็ได้ (ขวด/กก./ลิตร)</para>
///
/// <para>กติกา (ตัวตัดสินเดียว <see cref="OcrLineQuantityGuard.Decide"/>): (ก) round(ยอด ÷ จำนวน, 2) × จำนวน = ยอดทุกสตางค์ ⇒ คงจำนวน + ราคานั้น (แจ้ง) ·
/// (ข) ไม่ลงตัว + หลักฐานมิเตอร์ (ป้าย "หน่วยที่ใช้" · หน่วย(ค่าไฟ)/kWh/ลบ.ม./m3/นาที/GB/MB/ครั้ง(โทร)/เดือน · ผู้ขายสาธารณูปโภค) ⇒ 1 × ยอด ·
/// (ค) ไม่งั้น ⇒ คงจำนวน · ราคาปัด 2 ตำแหน่ง · เศษเป็นส่วนลดบรรทัด · แท็กบล็อกบอกส่วนต่างเป็นสตางค์</para>
/// </summary>
public class OcrGoodsQuantityKeptTests
{
    private static OcrLinePaper Paper(string? text) => OcrLineQuantityGuard.Read(text);

    private static DocumentLineRequest L(decimal q, decimal up, decimal disc = 0m)
        => new(Description: "x", Quantity: q, Unit: "ชิ้น", UnitPrice: up, DiscountPercent: 0m, VatRate: 0m,
            WithholdingTaxRate: 0m, AccountId: null, DiscountAmount: disc);

    // ── P0: ราคาว่าง · จำนวน ≠ 1 · ราคาไม่พิมพ์บนแถว ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]                                           // ไม่มีข้อความกระดาษ
    [InlineData("น้ำดื่ม 600ml\nขวด\n12\n300.00")]                // Azure อ่านแบบคอลัมน์ (column-major)
    [InlineData("น้ำดื่ม 600ml 12 ขวด 2S.OO 300.00")]             // Tesseract อ่านราคา 25.00 เพี้ยน
    [InlineData("น้ำดื่ม 600ml 12 300.00")]                       // ไม่มีคอลัมน์ราคา
    public void น้ำดื่ม12ขวด300_ราคาว่าง_คง12คูณ25_แจ้งไม่บล็อก_ไม่ต่อคำอธิบาย(string? paper)
    {
        var d = OcrLineQuantityGuard.Decide(12m, null, 300m, null, Paper(paper), "น้ำดื่ม 600ml", "ขวด");
        Assert.Equal(OcrLineQuantityAction.FillUnitPrice, d.Action);
        Assert.Null(d.Quantity);                     // คง 12
        Assert.Equal(25.00m, d.UnitPrice);
        Assert.Null(d.Description);                  // ไม่มี "(12 ขวด)"
        Assert.Null(d.Unit);
        Assert.False(d.Blocking);
        Assert.StartsWith(OcrLineQuantityGuard.NoteTag, d.Note);
        Assert.DoesNotContain("มิเตอร์", d.Note);    // ป้ายกลาง (P2: เดิมพูดว่า "มิเตอร์" บนบรรทัดสินค้า)
        Assert.Equal(300m, DocumentService.PreviewTotals(new[] { L(12m, d.UnitPrice!.Value) }, false, 0m).Net);
    }

    [Fact]
    public void หมูสับ1_235กก_98_80_คง1_235คูณ80_ดีเซล30_512ลิตร_990_09_คงจำนวน_ส่วนลด2สตางค์_บล็อก()
    {
        var pork = OcrLineQuantityGuard.Decide(1.235m, null, 98.80m, null, Paper("หมูสับ 1.235 กก. 98.80"), "หมูสับ", "กก.");
        Assert.Equal((OcrLineQuantityAction.FillUnitPrice, (decimal?)null, 80.00m), (pork.Action, pork.Quantity, pork.UnitPrice!.Value));
        Assert.False(pork.Blocking);

        var diesel = OcrLineQuantityGuard.Decide(30.512m, null, 990.09m, null, Paper("ดีเซล 30.512 ลิตร 990.09"), "ดีเซล", "ลิตร");
        Assert.Equal(OcrLineQuantityAction.RoundedUnitPrice, diesel.Action);
        Assert.Null(diesel.Quantity);
        Assert.Equal(32.45m, diesel.UnitPrice);      // 990.09 ÷ 30.512 = 32.4492
        Assert.Equal(0.02m, diesel.LineDiscount);    // 30.512 × 32.45 = 990.11
        Assert.True(diesel.Blocking);
        Assert.Contains("ส่วนต่าง 2 สตางค์", diesel.Note);
        Assert.Contains("⟦30.512×32.45⟧", diesel.Note);
        Assert.DoesNotContain("มิเตอร์", diesel.Note);
        Assert.Null(diesel.Description);
        // บรรทัดลงตัวตามสูตรเอกสาร (ComputeLineAmounts) ⇒ บันทึกซ้ำ 990.09 ทุกสตางค์
        Assert.Equal(990.09m, DocumentService.PreviewTotals(new[] { L(30.512m, 32.45m, 0.02m) }, false, 0m).Net);
    }

    [Fact]
    public void ปัดปกติแล้วต่ำกว่ายอด_ราคาปัดขึ้น1สตางค์_ส่วนลดไม่ติดลบ()
    {
        // 250 ÷ 3 = 83.333 ⇒ 83.33 × 3 = 249.99 < 250 ⇒ 83.34 × 3 = 250.02 − 0.02 (ส่วนลดติดลบเก็บไม่ได้)
        var d = OcrLineQuantityGuard.Decide(3m, null, 250m, null, Paper("ถุงปุ๋ย 3 ถุง 250.00"), "ถุงปุ๋ย", "ถุง");
        Assert.Equal((OcrLineQuantityAction.RoundedUnitPrice, 83.34m, 0.02m), (d.Action, d.UnitPrice!.Value, d.LineDiscount!.Value));
        Assert.Equal(250m, DocumentService.PreviewTotals(new[] { L(3m, 83.34m, 0.02m) }, false, 0m).Net);
    }

    [Fact]
    public void จำนวนเท่ายอด_ลงตัวโดยบังเอิญ_แท็กบล็อก()
    {
        // engine อ่านยอดมาใส่ช่องจำนวน (300 × 1.00) — ลงตัวแต่ไม่ใช่หลักฐาน ⇒ บล็อก (รุ่น 5 เติมเงียบ)
        var d = OcrLineQuantityGuard.Decide(300m, null, 300m, null, Paper(null), "สินค้า");
        Assert.Equal((OcrLineQuantityAction.FillUnitPrice, 1.00m), (d.Action, d.UnitPrice!.Value));
        Assert.True(d.Blocking);
    }

    // ── P1: หน่วยสินค้าไม่ใช่หลักฐานมิเตอร์ (เคส ก — engine อ่านยอดมาใส่ช่องราคา) ─────────────────────────────────

    [Theory]
    [InlineData(12, 300.00, "น้ำดื่ม 12 ขวด 300.00", "น้ำดื่ม", 25.00)]
    [InlineData(5, 450.00, "หมูสับ 5 กก. 450.00", "หมูสับ", 90.00)]
    public void ราคาเท่ายอด_หน่วยสินค้าคู่จำนวน_คงจำนวน_ราคายอดหารจำนวน_แจ้ง(double qty, double amount, string paper, string desc, double price)
    {
        var d = OcrLineQuantityGuard.Decide((decimal)qty, (decimal)amount, (decimal)amount, null, Paper(paper), desc);
        Assert.Equal(OcrLineQuantityAction.FixUnitPrice, d.Action);
        Assert.Null(d.Quantity);
        Assert.Equal((decimal)price, d.UnitPrice);
        Assert.Null(d.Description);
        Assert.False(d.Blocking);
    }

    [Fact]
    public void ราคาเท่ายอด_สินค้าไม่ลงตัว_คงจำนวน_บล็อก_มิเตอร์ไม่ลงตัว_1คูณยอด()
    {
        var goods = OcrLineQuantityGuard.Decide(12m, 299.99m, 299.99m, null, Paper("น้ำดื่ม 12 ขวด 299.99"), "น้ำดื่ม");
        Assert.Equal(OcrLineQuantityAction.RoundedUnitPrice, goods.Action);
        Assert.Null(goods.Quantity);
        Assert.Equal((25.00m, 0.01m), (goods.UnitPrice!.Value, goods.LineDiscount!.Value));
        Assert.True(goods.Blocking);
        // ทิศตรงข้าม: บิลค่าไฟ "3,611 หน่วย" (หน่วย + คำอธิบายค่าไฟ = มิเตอร์) ⇒ 1 × ยอด + จำนวนในคำอธิบาย (ทีม OCR 8 คงเดิม)
        var meter = OcrLineQuantityGuard.Decide(3611m, 16351.48m, 16351.48m, null,
            Paper("การไฟฟ้าส่วนภูมิภาค ใบแจ้งค่าไฟฟ้า/ใบกำกับภาษี\nค่าไฟฟ้า 3,611 หน่วย 16,351.48\nรวมเงิน 16,351.48"), "ค่าไฟฟ้า");
        Assert.Equal(OcrLineQuantityAction.QuantityInDescription, meter.Action);
        Assert.Equal((1m, 16351.48m), (meter.Quantity!.Value, meter.UnitPrice!.Value));
        Assert.Equal("ค่าไฟฟ้า (3,611 หน่วย)", meter.Description);
        Assert.False(meter.Blocking);
        Assert.Contains("ตามมิเตอร์", meter.Note);
    }

    [Fact]
    public void หน่วยทั่วไปไม่มีบริบทค่าไฟ_ไม่ใช่มิเตอร์_มีบริบท_ใช่()
    {
        var goods = OcrLineQuantityGuard.Decide(12m, null, 299.99m, null, Paper("สินค้า 12 หน่วย 299.99"), "สินค้า");
        Assert.Equal(OcrLineQuantityAction.RoundedUnitPrice, goods.Action);
        var power = OcrLineQuantityGuard.Decide(12m, null, 299.99m, null, Paper("ค่าไฟฟ้า 12 หน่วย 299.99"), "ค่าไฟฟ้า");
        Assert.Equal(OcrLineQuantityAction.QuantityInDescription, power.Action);
        Assert.Equal("ค่าไฟฟ้า (12 หน่วย)", power.Description);
    }

    // ── P1: ด่านราคาเกิน 2 ตำแหน่ง ต้องถาม "ปัดแล้วลงตัวไหม" ──────────────────────────────────────────────────────

    [Fact]
    public void ราคา4ตำแหน่งที่ปัดแล้วลงตัว_ไม่แตะ_ปัดแล้วไม่ลงตัวแต่ยอดหารลงตัว_ราคานั้น()
    {
        // 1.235 × 80.0405 = 98.85 และ 1.235 × 80.04 = 98.85 — เดิม (13a4d788) ⇒ 1 × 98.85 พร้อมหมายเหตุ "98.85 ≠ 98.85"
        var tie = OcrLineQuantityGuard.Decide(1.235m, 80.0405m, 98.85m, null, Paper(null), "หมูสับ", "กก.");
        Assert.Equal(OcrLineQuantityAction.Keep, tie.Action);
        Assert.Null(tie.Note);
        Assert.Equal(98.85m, DocumentService.PreviewTotals(new[] { L(1.235m, 80.04m) }, false, 0m).Net);
        // 1.235 × 80.0449 = 98.8555 ⇒ 98.86 แต่ 1.235 × 80.04 = 98.85 ≠ 98.86 · 98.86 ÷ 1.235 = 80.05 ⇒ 98.86 ✓ ⇒ ราคา 80.05 (แจ้ง)
        var off = OcrLineQuantityGuard.Decide(1.235m, 80.0449m, 98.86m, null, Paper(null), "หมูสับ", "กก.");
        Assert.Equal((OcrLineQuantityAction.FixUnitPrice, (decimal?)null, 80.05m), (off.Action, off.Quantity, off.UnitPrice!.Value));
        Assert.False(off.Blocking);
        Assert.DoesNotContain("≠ ยอด 98.86 — บันทึกซ้ำแล้วยอดเลื่อน) ⇒ ระบบลง 1", off.Note);
    }

    // ── หลักฐานมิเตอร์จากผู้ขาย · หน่วยสินค้าชนะผู้ขาย ─────────────────────────────────────────────────────────────

    [Fact]
    public void ผู้ขายสาธารณูปโภค_เป็นหลักฐานมิเตอร์_หน่วยสินค้าบนแถวชนะผู้ขาย()
    {
        var bySeller = OcrLineQuantityGuard.Decide(4272m, null, 19381.50m, null, Paper("ค่าบริการ 4,272.00 19,381.50"), "ค่าบริการ", null, utilitySeller: true);
        Assert.Equal(OcrLineQuantityAction.QuantityInDescription, bySeller.Action);
        Assert.Equal("ค่าบริการ (จำนวน 4,272)", bySeller.Description);
        var noSeller = OcrLineQuantityGuard.Decide(4272m, null, 19381.50m, null, Paper("ค่าบริการ 4,272.00 19,381.50"), "ค่าบริการ", null);
        Assert.Equal(OcrLineQuantityAction.RoundedUnitPrice, noSeller.Action);
        var goods = OcrLineQuantityGuard.Decide(12m, null, 299.99m, null, Paper("สายไฟ 12 ม้วน 299.99"), "สายไฟ", null, utilitySeller: true);
        Assert.Equal(OcrLineQuantityAction.RoundedUnitPrice, goods.Action);
    }

    [Theory]
    [InlineData("0994000165501", null, true)]                      // กฟภ. (บิลจริง XH0712609004157)
    [InlineData("0-9940-00165-50-1", null, true)]
    [InlineData(null, "การไฟฟ้าส่วนภูมิภาค สาขาบางพลี", true)]
    [InlineData(null, "การประปานครหลวง", true)]
    [InlineData(null, "บริษัท ทริปเปิลที บรอดแบนด์ จำกัด (มหาชน)", true)]
    [InlineData(null, "PEA ENCOM", true)]
    [InlineData(null, "True Coffee", false)]                        // แบรนด์สั้นขายสินค้า
    [InlineData(null, "Speaker Shop", false)]                       // "pea" ในคำอื่น
    [InlineData("0105556000025", "ร้านวัสดุ", false)]
    [InlineData(null, null, false)]
    public void ตัวตัดสินผู้ขายสาธารณูปโภค_กุญแจก่อนชื่อ_ไม่ใช่ความคล้าย(string? taxId, string? name, bool expected)
        => Assert.Equal(expected, OcrUtilityVendor.Is(taxId, name));

    // ── Sanitize (ทางเข้าจริงของทุกเส้น) · บันทึกซ้ำ · พรีวิวนำเข้าสต็อก ────────────────────────────────────────────

    private static OcrExtractedData GoodsScan(decimal qty, decimal amount, string desc = "น้ำดื่ม 600ml", string unit = "ขวด")
    {
        var d = new OcrExtractedData { TotalAmount = amount, VatAmount = 0m };
        d.Items.Add(new OcrExtractedLineItem { Description = desc, Quantity = qty, UnitPrice = null, Amount = amount, Unit = unit });
        return d;
    }

    [Theory]
    [InlineData(300.00, 25.00, null)]
    [InlineData(299.99, 25.00, 0.01)]
    public void Sanitize_สินค้าคงจำนวน12_สร้างซ้ำจากJSONไม่เปลี่ยน_พรีวิวสต็อกได้12(double amount, double price, double? discount)
    {
        var d = GoodsScan(12m, (decimal)amount);
        OcrService.SanitizeVatSplitArtifacts(d, false, "น้ำดื่ม 600ml\nขวด\n12\n" + amount.ToString("0.00"));
        var it = Assert.Single(d.Items);
        Assert.Equal(12m, it.Quantity);
        Assert.Equal((decimal)price, it.UnitPrice);
        Assert.Equal((decimal?)discount, it.LineDiscountAmount);
        Assert.Equal("น้ำดื่ม 600ml", it.Description);
        Assert.Equal("ขวด", it.Unit);
        Assert.Single(d.ReasoningTrace, t => t.StartsWith(OcrLineQuantityGuard.NoteTag, StringComparison.Ordinal)
                                            || t.StartsWith(OcrLineQuantityGuard.Tag + " ", StringComparison.Ordinal));

        // สร้าง/พรีวิว/ดึงรายการซ้ำ = Sanitize อีกรอบบน ExtractedItemsJson (มี/ไม่มีข้อความกระดาษ) ⇒ ค่าเดิม ไม่มีหมายเหตุใหม่
        var json = OcrService.SerializeExtractedItems(d.Items.ToList());
        foreach (var text in new[] { "น้ำดื่ม 600ml\nขวด\n12\n" + amount.ToString("0.00"), null })
        {
            var again = new OcrExtractedData();
            foreach (var x in System.Text.Json.JsonSerializer.Deserialize<List<OcrExtractedLineItem>>(json)!) again.Items.Add(x);
            OcrService.SanitizeVatSplitArtifacts(again, false, text);
            var a = Assert.Single(again.Items);
            Assert.Equal((12m, (decimal)price, (decimal?)discount), (a.Quantity!.Value, a.UnitPrice!.Value, a.LineDiscountAmount));
            Assert.Empty(again.ReasoningTrace);
        }
        // เปิดแก้แล้วบันทึก (ComputeLineAmounts) สองรอบ = ยอดบนกระดาษทุกสตางค์
        var saved = DocumentService.PreviewTotals(new[] { L(12m, (decimal)price, (decimal)(discount ?? 0)) }, false, 0m);
        Assert.Equal((decimal)amount, saved.Net);
        Assert.Equal(saved, DocumentService.PreviewTotals(new[] { L(12m, (decimal)price, (decimal)(discount ?? 0)) }, false, 0m));

        // พรีวิวนำเข้าสต็อก (OcrController.StockPreview) อ่าน ExtractedItemsJson เป็น OcrLineItemDto แล้วตั้ง detectedQty = line.Quantity ·
        // ทุนต่อหน่วย = EffectiveUnitCost — 13a4d788 ได้ 1 ขวด ทุน 300 (สต็อกขาด 11 · ทุนเฉลี่ยเพี้ยน 12 เท่า)
        var dto = System.Text.Json.JsonSerializer.Deserialize<List<OcrLineItemDto>>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var line = Assert.Single(dto);
        var detectedQty = line.Quantity ?? ProductMatcher.DetectQuantity(line.Description);
        Assert.Equal(12m, detectedQty);
        var cost = OcrEtaxLineNormalizer.EffectiveUnitCost(line.Quantity, line.UnitPrice, line.Amount, line.LineDiscountAmount);
        Assert.Equal(Math.Round((decimal)amount / 12m, 4, MidpointRounding.AwayFromZero), cost);
        Assert.Equal("น้ำดื่ม 600ml", OcrLineQuantityGuard.DescriptionForMatching(line.Description, line.Quantity, line.Unit));
    }

    [Fact]
    public void Sanitize_บิลค่าไฟจริงPEA_ยัง1คูณยอด_ผู้ขายจากเลขผู้เสียภาษีตามไปเส้นสร้างซ้ำ()
    {
        var d = new OcrExtractedData { VendorTaxId = "0994000165501", SubTotal = 19381.50m, VatAmount = 1356.71m, TotalAmount = 20738.21m };
        d.Items.Add(new OcrExtractedLineItem { Description = "ค่าไฟฟ้า ประจำเดือน 08/2569", Quantity = 4272m, Amount = 19381.50m });
        OcrService.SanitizeVatSplitArtifacts(d, false, OcrPaperSamples.PeaElectricityBill0869);
        var it = Assert.Single(d.Items);
        Assert.Equal((1m, 19381.50m), (it.Quantity!.Value, it.UnitPrice!.Value));
        Assert.Equal("ค่าไฟฟ้า ประจำเดือน 08/2569 (4,272 หน่วย)", it.Description);
        Assert.Equal(OcrLineQuantityGuard.LumpSumUnit, it.Unit);
        // คีย์จับคู่/ตัวเรียน = ไม่มีท้ายจำนวนหน่วย (เดือนหน้าได้คีย์เดียวกันแม้หน่วยเปลี่ยน)
        Assert.Equal("ค่าไฟฟ้า ประจำเดือน 08/2569", OcrLineQuantityGuard.DescriptionForMatching(it.Description, it.Quantity, it.Unit));
        Assert.Equal("ค่าไฟฟ้า ประจำเดือน 08/2569",
            OcrLearnerDescriptionKey.For(OcrLearnerDescriptionKey.Descriptions(d.Items.Select(i => (i.Description, i.Quantity, i.Unit)))));
        // ใบไม่มีบรรทัด: คีย์ "#ไม่มีรายการ#<ชนิด>" ของทีม EC2 ไม่ถูกแตะ
        Assert.Equal(OcrLearnerDescriptionKey.LinelessPrefix + "Receipt",
            OcrLearnerDescriptionKey.For(OcrLearnerDescriptionKey.Descriptions(Array.Empty<(string?, decimal?, string?)>()), "Receipt"));
    }

    // ── คีย์จับคู่สินค้า/ตัวเรียน · ความยาวคำอธิบาย ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ค่าไฟฟ้า (4,272 หน่วย)", 1, "รายการ", "ค่าไฟฟ้า")]
    [InlineData("ค่าน้ำประปา (25 ลบ.ม. × 9.00)", 1, "รายการ", "ค่าน้ำประปา")]
    [InlineData("ค่าบริการ (จำนวน 4,272)", 1, "รายการ", "ค่าบริการ")]
    [InlineData("น้ำดื่ม 600ml (12 ขวด)", 1, "รายการ", "น้ำดื่ม 600ml")]          // ท้ายที่ทีม 8 persist ไว้วันนี้ (จำนวน 1 · รายการ) ก็ตัดได้
    [InlineData("น้ำดื่ม 600ml (12 ขวด)", 1, "แพ็ค", "น้ำดื่ม 600ml (12 ขวด)")]   // ชื่อสินค้าจริง (หน่วยอื่น) ⇒ ไม่แตะ
    [InlineData("น้ำดื่ม 600ml (12 ขวด)", 2, "รายการ", "น้ำดื่ม 600ml (12 ขวด)")]  // จำนวน ≠ 1 ⇒ ไม่ใช่บรรทัด 1 × ยอด ⇒ ไม่แตะ
    [InlineData("Flash Drive (64 GB)", 3, "ชิ้น", "Flash Drive (64 GB)")]
    [InlineData("(4,272 หน่วย)", 1, "รายการ", "(4,272 หน่วย)")]                    // ไม่มีคำอธิบายเหลือ ⇒ คงไว้
    public void คีย์จับคู่_ตัดท้ายเฉพาะบรรทัด1คูณยอดของตัวกัน(string desc, double qty, string unit, string expected)
        => Assert.Equal(expected, OcrLineQuantityGuard.DescriptionForMatching(desc, (decimal)qty, unit));

    [Fact]
    public void คำอธิบายยาวเกินคอลัมน์_ตัดคำอธิบาย_คงท้ายครบ_ไม่เกินความยาวคอลัมน์()
    {
        var longDesc = "ค่าไฟฟ้า " + new string('ก', 1200);
        var d = OcrLineQuantityGuard.Decide(4272m, null, 19381.50m, null, Paper("หน่วยที่ใช้ 4272.0019,381.50"), longDesc);
        Assert.Equal(OcrLineQuantityAction.QuantityInDescription, d.Action);
        Assert.True(d.Description!.Length <= DocumentLine.DescriptionMaxLength);
        Assert.EndsWith("… (4,272 หน่วย)", d.Description);
        Assert.StartsWith("ค่าไฟฟ้า ", d.Description);
        // ตัดกลับเป็นคีย์ได้ · ต่อซ้ำไม่ซ้อน (idempotent)
        Assert.Equal(d.Description, OcrLineQuantityGuard.QuantityDescription(d.Description, 4272m, null));
        // สั้นพอดี ⇒ ไม่ตัด
        Assert.Equal("ค่าไฟฟ้า (4,272 หน่วย)", OcrLineQuantityGuard.FitDescription("ค่าไฟฟ้า", "(4,272 หน่วย)"));
        Assert.Equal(DocumentLine.DescriptionMaxLength,
            OcrLineQuantityGuard.FitDescription(new string('x', 2000), "(1 หน่วย)").Length);
    }
}
