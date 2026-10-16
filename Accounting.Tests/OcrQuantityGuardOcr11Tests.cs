using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Ocr;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ทีม OCR 11 — ฝ่ายค้าน OCR 9 (bbf596e9..5a58139e): หลักฐานมิเตอร์ต้องอยู่บนบรรทัด · เศษปัดราคาต่อหน่วยซื่อสัตย์ · ตัวตัดท้ายตัดเฉพาะเครื่องหมายของตัวกัน** (2026-10-16)
///
/// <para>ทุก probe ของฝ่ายค้านเป็นเทสต์สองทิศ: บรรทัดที่พัง (คอนกรีต 2.75 ลบ.ม. · เคส iPhone ของ AWN · ค่าน้ำดื่ม 12 · น็อต 100,000 ตัว · "Flash Drive (64 GB)")
/// กลับมาถูก และบรรทัดที่ถูกอยู่แล้ว (บิล PEA 1 × 19,381.50 "(4,272 หน่วย)" · บิลประปา · ดีเซล 30.512 × 32.45 · น้ำดื่ม 12 × 25 · สมาชิก 12 เดือน) ไม่ถูกแตะ</para>
/// </summary>
public class OcrQuantityGuardOcr11Tests
{
    private static OcrLinePaper Paper(string? text) => OcrLineQuantityGuard.Read(text);

    private static DocumentLineRequest L(decimal q, decimal up, decimal disc = 0m)
        => new(Description: "x", Quantity: q, Unit: "ชิ้น", UnitPrice: up, DiscountPercent: 0m, VatRate: 0m,
            WithholdingTaxRate: 0m, AccountId: null, DiscountAmount: disc);

    // ── P1-1: ลบ.ม. เป็นมิเตอร์เฉพาะบริบทน้ำประปา ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("คอนกรีตผสมเสร็จ 240 ksc 2.75 ลบ.ม. 1,500.00", null)]
    [InlineData(null, "ลบ.ม.")]                                       // หน่วยจาก engine ไม่มีข้อความ (ผลเดียวกันตอน probe)
    [InlineData("ค่าน้ำประปา หน้างาน 1 รายการ\nคอนกรีตผสมเสร็จ 240 ksc 2.75 ลบ.ม. 1,500.00", "ลบ.ม.")]   // ใบพูดถึงน้ำประปา แต่บรรทัดเป็นวัสดุ
    public void คอนกรีต2_75ลบม_คงจำนวน_ไม่ใช่1คูณยอด_เศษปัด2สตางค์_บล็อก(string? paper, string? unit)
    {
        var d = OcrLineQuantityGuard.Decide(2.75m, null, 1500m, null, Paper(paper), "คอนกรีตผสมเสร็จ 240 ksc", unit);
        Assert.Equal(OcrLineQuantityAction.RoundedUnitPrice, d.Action);
        Assert.Null(d.Quantity);                       // คง 2.75 (เดิม 1 × 1,500 "(2.75 ลบ.ม.)" ไม่บล็อก)
        Assert.Null(d.Description);
        Assert.Equal(545.46m, d.UnitPrice);            // 1,500 ÷ 2.75 = 545.4545 ⇒ 545.45 × 2.75 = 1,499.99 < ยอด ⇒ 545.46
        Assert.Equal(0.02m, d.LineDiscount);
        Assert.True(d.Blocking);
        Assert.Contains(OcrLineQuantityGuard.RoundingRemainderPhrase, d.Note);
        Assert.Equal(1500m, DocumentService.PreviewTotals(new[] { L(2.75m, 545.46m, 0.02m) }, false, 0m).Net);
    }

    [Fact]
    public void บิลน้ำประปา_ยัง1คูณยอด_จากคำอธิบาย_ผู้ขายการประปา_หรือใบพูดถึงน้ำประปา()
    {
        // คำอธิบายบอกน้ำประปา (บิล กปน./กปภ.) ⇒ 1 × ยอด "(25 ลบ.ม.)" (ทีม OCR 8 เดิม)
        var mwa = OcrLineQuantityGuard.Decide(25m, null, 246.13m, null, Paper("ค่าน้ำประปา ประจำเดือน 08/2569 25 ลบ.ม. 246.13"),
            "ค่าน้ำประปา ประจำเดือน 08/2569", null);
        Assert.Equal(OcrLineQuantityAction.QuantityInDescription, mwa.Action);
        Assert.Equal("ค่าน้ำประปา ประจำเดือน 08/2569 (25 ลบ.ม.)", mwa.Description);
        Assert.False(mwa.Blocking);
        // คำอธิบายไม่บอก แต่ผู้ขายคือการประปา (ชนิดบริการ = บริบทของ ลบ.ม.)
        Assert.Equal(OcrUtilityKind.Water, OcrUtilityVendor.KindOf(null, "การประปาส่วนภูมิภาค สาขาชลบุรี"));
        var pwa = OcrLineQuantityGuard.Decide(25m, null, 246.13m, null, Paper("ปริมาณ 25 ลบ.ม. 246.13"), "Water charge", "ลบ.ม.",
            utilitySeller: OcrUtilityKind.Water);
        Assert.Equal(OcrLineQuantityAction.QuantityInDescription, pwa.Action);
        // ทิศตรงข้าม: ผู้ขายไฟฟ้าไม่ใช่บริบทของ ลบ.ม.
        var wrongSeller = OcrLineQuantityGuard.Decide(25m, null, 246.13m, null, Paper("ปริมาณ 25 ลบ.ม. 246.13"), "Water charge", "ลบ.ม.",
            utilitySeller: OcrUtilityKind.Electricity);
        Assert.Equal(OcrLineQuantityAction.RoundedUnitPrice, wrongSeller.Action);
        // ใบพูดถึงน้ำประปา + บรรทัดไม่ใช่วัสดุก่อสร้าง ⇒ บริบทของใบ
        var paperCtx = OcrLineQuantityGuard.Decide(25m, null, 246.13m, null, Paper("ใบแจ้งค่าน้ำประปา\nปริมาณ 25 ลบ.ม. 246.13"), "Water charge", "ลบ.ม.");
        Assert.Equal(OcrLineQuantityAction.QuantityInDescription, paperCtx.Action);
        // ทรายบนใบเดียวกัน ⇒ ไม่ใช่มิเตอร์
        var sand = OcrLineQuantityGuard.Decide(3m, null, 1000m, null, Paper("ใบแจ้งค่าน้ำประปา\nทรายหยาบ 3 ลบ.ม. 1,000.00"), "ทรายหยาบ", "ลบ.ม.");
        Assert.Equal(OcrLineQuantityAction.RoundedUnitPrice, sand.Action);
        Assert.Null(sand.Quantity);
    }

    // ── P1-2: ผู้ขายสาธารณูปโภคอย่างเดียวไม่พาไป 1 × ยอด ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("บริษัท แอดวานซ์ ไวร์เลส เน็ทเวอร์ค จำกัด", "เคส iPhone 16", 3, 2775.70, 925.24, 0.02)]   // 3 × 990 ถอด VAT
    [InlineData("บริษัท ทรู มูฟ เอช ยูนิเวอร์แซล คอมมิวนิเคชั่น จำกัด", "หูฟัง Bluetooth", 2, 1851.41, 925.71, 0.01)]
    public void ผู้ขายAWNทรูมูฟเอช_ขายสินค้า_คงจำนวน_ผ่านSanitize(string vendor, string desc, int qty, double amount, double price, double disc)
    {
        Assert.Equal(OcrUtilityKind.Telecom, OcrUtilityVendor.KindOf(null, vendor));
        var d = new OcrExtractedData { VendorName = vendor, TotalAmount = (decimal)amount, VatAmount = 0m };
        d.Items.Add(new OcrExtractedLineItem { Description = desc, Quantity = qty, Amount = (decimal)amount, Unit = "ชิ้น" });
        OcrService.SanitizeVatSplitArtifacts(d, false, $"{desc} {qty} {amount:#,0.00}");
        var it = Assert.Single(d.Items);
        Assert.Equal((decimal)qty, it.Quantity);                       // เดิม 1 × ยอด "(จำนวน 3)" ไม่บล็อก
        Assert.Equal(desc, it.Description);
        Assert.Equal("ชิ้น", it.Unit);
        Assert.Equal((decimal)price, it.UnitPrice);
        Assert.Equal((decimal)disc, it.LineDiscountAmount);
        Assert.Equal((decimal)disc, it.UnitPriceRoundingResidual);
        Assert.Contains(d.ReasoningTrace, t => t.StartsWith(OcrLineQuantityGuard.Tag + " ", StringComparison.Ordinal));
        Assert.Equal((decimal)amount, DocumentService.PreviewTotals(new[] { L(qty, (decimal)price, (decimal)disc) }, false, 0m).Net);
    }

    [Fact]
    public void บิลค่าไฟจริงPEA_ยัง1คูณ19381_50_สี่พันสองร้อยเจ็ดสิบสองหน่วย_มีหรือไม่มีเลขผู้เสียภาษี()
    {
        foreach (var taxId in new[] { "0994000165501", null })
        {
            var d = new OcrExtractedData { VendorTaxId = taxId, SubTotal = 19381.50m, VatAmount = 1356.71m, TotalAmount = 20738.21m };
            d.Items.Add(new OcrExtractedLineItem { Description = "ค่าไฟฟ้า ประจำเดือน 08/2569", Quantity = 4272m, Amount = 19381.50m });
            OcrService.SanitizeVatSplitArtifacts(d, false, OcrPaperSamples.PeaElectricityBill0869);
            var it = Assert.Single(d.Items);
            Assert.Equal((1m, 19381.50m), (it.Quantity!.Value, it.UnitPrice!.Value));
            Assert.Equal("ค่าไฟฟ้า ประจำเดือน 08/2569 (4,272 หน่วย)", it.Description);
            Assert.Equal(OcrLineQuantityGuard.LumpSumUnit, it.Unit);
            Assert.Null(it.UnitPriceRoundingResidual);
            Assert.Equal("ค่าไฟฟ้า ประจำเดือน 08/2569", OcrLineQuantityGuard.DescriptionForMatching(it.Description, it.Quantity, it.Unit));
            Assert.True(OcrLineQuantityGuard.LumpSumSuffix(it.Description, it.Quantity, it.Unit)!.Value.Meter);
        }
    }

    // ── P1-3: ค่าน้ำดื่ม / น้ำแข็ง / น้ำมัน ไม่ใช่น้ำประปา ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ค่าน้ำดื่ม 600ml", null)]
    [InlineData("ค่าน้ำแข็ง", null)]
    [InlineData("ค่าน้ำ ดื่ม", null)]
    [InlineData("ค่าน้ำมันดีเซล", "ลิตร")]                // เดิม ลบ.ม. (กฎน้ำประปามาก่อนกฎน้ำมัน)
    [InlineData("ท่อประปา PVC 1/2", null)]
    [InlineData("ค่าน้ำประปา ประจำเดือน", "ลบ.ม.")]
    [InlineData("ค่าน้ำ ประจำเดือน 08/2569", "ลบ.ม.")]
    [InlineData("การประปาส่วนภูมิภาค ค่าบริการ", "ลบ.ม.")]
    [InlineData("ค่าไฟฟ้า", "หน่วย")]
    public void UnitInferrer_ค่าน้ำดื่มไม่ใช่ลบม_น้ำประปายังเป็นลบม(string desc, string? expected)
        => Assert.Equal(expected, UnitInferrer.Infer(desc));

    [Fact]
    public void ค่าน้ำดื่ม12ขวด299_ไม่มีหน่วย_คงจำนวน12_ไม่ใช่1คูณยอด12ลบม()
    {
        var d = OcrLineQuantityGuard.Decide(12m, null, 299m, null, Paper(null), "ค่าน้ำดื่ม 600ml", null);
        Assert.Equal(OcrLineQuantityAction.RoundedUnitPrice, d.Action);
        Assert.Null(d.Quantity);
        Assert.Null(d.Description);                    // เดิม "ค่าน้ำดื่ม 600ml (12 ลบ.ม.)"
        Assert.Equal(24.92m, d.UnitPrice);             // 299 ÷ 12 = 24.9167 ⇒ 24.92 × 12 = 299.04
        Assert.Equal(0.04m, d.LineDiscount);
        Assert.True(d.Blocking);
    }

    // ── บรรทัดที่ถูกอยู่แล้วต้องไม่ถูกแตะ ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ทิศตรงข้าม_ดีเซล_น้ำดื่ม12คูณ25_สมาชิก12เดือน_ไม่เปลี่ยน()
    {
        var diesel = OcrLineQuantityGuard.Decide(30.512m, null, 990.09m, null, Paper("ดีเซล 30.512 ลิตร 990.09"), "ดีเซล", "ลิตร");
        Assert.Equal((OcrLineQuantityAction.RoundedUnitPrice, (decimal?)null, 32.45m, 0.02m),
            (diesel.Action, diesel.Quantity, diesel.UnitPrice!.Value, diesel.LineDiscount!.Value));
        Assert.True(diesel.Blocking);
        Assert.Contains("ส่วนต่าง 2 สตางค์", diesel.Note);
        Assert.Contains(OcrLineQuantityGuard.RoundingRemainderPhrase, diesel.Note);

        var water = OcrLineQuantityGuard.Decide(12m, null, 300m, null, Paper("น้ำดื่ม 600ml 12 ขวด 300.00"), "น้ำดื่ม 600ml", "ขวด");
        Assert.Equal((OcrLineQuantityAction.FillUnitPrice, (decimal?)null, 25.00m), (water.Action, water.Quantity, water.UnitPrice!.Value));
        Assert.False(water.Blocking);

        var sub = OcrLineQuantityGuard.Decide(12m, null, 9990m, null, Paper("สมาชิก 12 เดือน 9,990.00"), "ค่าสมาชิกรายปี", "เดือน");
        Assert.Equal((OcrLineQuantityAction.FillUnitPrice, (decimal?)null, 832.50m), (sub.Action, sub.Quantity, sub.UnitPrice!.Value));
    }

    // ── P2-1 / P2-2: เศษปัดราคาต่อหน่วยมีเพดาน · ใต้เพดานติดป้ายเศษ ไม่ใช่ส่วนลด ────────────────────────────────────

    [Theory]
    [InlineData(0.02, 990.09, true)]          // ดีเซล
    [InlineData(0.01, 299.99, true)]
    [InlineData(1.00, 1000.00, true)]         // ชนเพดานทั้งสองข้อพอดี
    [InlineData(1.01, 100000.00, false)]      // เกิน 1 บาท
    [InlineData(0.60, 100.00, false)]         // เกิน 0.5%
    [InlineData(13.38, 19381.50, false)]      // สติกเกอร์ 4,272 แผ่น
    [InlineData(500.00, 500.00, false)]       // น็อต 100,000 ตัว
    [InlineData(-0.01, 100.00, false)]
    public void เพดานเศษปัด_1บาทและครึ่งเปอร์เซ็นต์_ตัวเลขเดียวกับเศษถอดVAT(double remainder, double amount, bool expected)
        => Assert.Equal(expected, OcrLineQuantityGuard.RoundingRemainderWithinCap((decimal)remainder, (decimal)amount));

    [Theory]
    [InlineData(100000, 500.00, "น็อต M6", "ตัว", 0.01)]
    [InlineData(4272, 19381.50, "สติกเกอร์ฉลาก", "แผ่น", 4.54)]
    public void เศษเกินเพดาน_คงจำนวน_บรรทัดสอดคล้อง_ป้ายเศษปัด_บล็อกแม้คนอนุมัติ_รันซ้ำไม่แตะ(int qty, double amount, string desc, string unit, double price)
    {
        // ★ ทีม OCR 12 (ฝ่ายค้านรอบ 3 P2-1 · ตั้งใจพลิกจากทีม 11): เดิมเก็บ 100,000 × 0.01 ยอด 500 (ไม่สอดคล้อง — CLAUDE #2 A) ⇒ Update คิดใหม่เป็น 1,000 เงียบ ๆ ·
        // UnitPrice เก็บ 2 ตำแหน่ง ⇒ ราคาปัด + เศษในช่องส่วนลด (ป้ายเศษปัด) + รหัสเกินเพดาน ⇒ ด่านอนุมัติโดยคนบล็อกจนแก้บรรทัด
        var d = OcrLineQuantityGuard.Decide(qty, null, (decimal)amount, null, Paper(null), desc, unit);
        Assert.Equal(OcrLineQuantityAction.RoundedUnitPrice, d.Action);
        Assert.Null(d.Quantity);
        Assert.Equal((decimal)price, d.UnitPrice);
        var disc = Assert.NotNull(d.LineDiscount);
        Assert.Equal((decimal)amount, Math.Round(qty * (decimal)price, 2, MidpointRounding.AwayFromZero) - disc);   // round(จำนวน × ราคา) − ส่วนลด = ยอดกระดาษ
        Assert.True(d.Blocking);
        Assert.StartsWith(OcrLineQuantityGuard.Tag + " " + OcrLineQuantityGuard.RoundingOverCapCode, d.Note);
        Assert.Contains(OcrLineQuantityGuard.RoundingRemainderPhrase, d.Note);
        Assert.Contains(OcrLineQuantityGuard.Fingerprint(qty, (decimal)price), d.Note);

        // รันซ้ำบน ExtractedItemsJson (สร้างเอกสาร/พรีวิว/ดึงรายการซ้ำ): บรรทัดสอดคล้องแล้ว ⇒ ไม่แตะ (ห้ามหารจำนวนใหม่ — เดิม 500 ÷ 0.01 = 50,000)
        var again = OcrLineQuantityGuard.Decide(qty, d.UnitPrice, (decimal)amount, d.LineDiscount, Paper(null), desc, unit);
        Assert.Equal(OcrLineQuantityAction.Keep, again.Action);
        // สแกนรุ่น 9 ที่ persist บรรทัดไม่สอดคล้อง (ไม่มีส่วนลด) ⇒ เส้นสร้างเอกสารรันซ้ำได้รูปสอดคล้อง
        var legacy = OcrLineQuantityGuard.Decide(qty, d.UnitPrice, (decimal)amount, null, Paper(null), desc, unit);
        Assert.Equal((d.UnitPrice, d.LineDiscount, d.Note), (legacy.UnitPrice, legacy.LineDiscount, legacy.Note));

        // ด่านอนุมัติเอง: บรรทัดยังถือค่าของระบบ ⇒ บล็อกต่อ · ด่านอนุมัติโดยคน: บล็อก + ทางไปต่อ
        var systemLine = new OcrLiveLine(qty, (decimal)price, 0m, disc, (decimal)amount, 0m);
        Assert.Equal(d.Note, OcrLineQuantityGuard.LiveNotes(d.Note, new[] { systemLine }, false, true));
        var human = OcrPostingReadiness.EvaluateHumanApproval(d.Note, new[] { systemLine });
        Assert.False(human.CanAutoApprove);
        Assert.Contains("แกะใหม่", human.Reason);
        // ทิศตรงข้าม: คนเปลี่ยนเป็นหน่วยแพ็ก/ราคาจริง ⇒ ปลด
        var fixedLine = new OcrLiveLine(qty / 100m, Math.Round((decimal)amount / (qty / 100m), 2, MidpointRounding.AwayFromZero), 0m, 0m, (decimal)amount, 0m);
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(d.Note, new[] { fixedLine }).CanAutoApprove);
    }

    [Fact]
    public void เศษใต้เพดาน_ป้ายหน้ารีวิว_เศษจากการปัดราคาต่อหน่วย_ไม่ใช่ส่วนลด_ผู้ใช้แก้แล้วล้าง()
    {
        var d = new OcrExtractedData { TotalAmount = 990.09m, VatAmount = 0m };
        d.Items.Add(new OcrExtractedLineItem { Description = "ดีเซล", Quantity = 30.512m, Amount = 990.09m, Unit = "ลิตร" });
        OcrService.SanitizeVatSplitArtifacts(d, false, "ดีเซล 30.512 ลิตร 990.09");
        var it = Assert.Single(d.Items);
        Assert.Equal((32.45m, 0.02m, 0.02m), (it.UnitPrice!.Value, it.LineDiscountAmount!.Value, it.UnitPriceRoundingResidual!.Value));
        var label = OcrEtaxLineNormalizer.DiscountLabel(it.LineDiscountAmount, it.PriceIncludesVat, it.VatStripResidual, it.UnitPriceRoundingResidual);
        Assert.StartsWith(OcrLineQuantityGuard.RoundingRemainderPhrase, label);
        Assert.DoesNotContain("ส่วนลดของบรรทัด", label);
        // ทิศตรงข้าม: ส่วนลดจากกระดาษจริงยังเป็น "ส่วนลดของบรรทัด"
        Assert.Equal("ส่วนลดของบรรทัด (ก่อน VAT)", OcrEtaxLineNormalizer.DiscountLabel(293m, false, null, null));

        // persist ⇒ รันซ้ำตอนสร้างเอกสาร: ค่าเดิม ธงเดิม ไม่มีหมายเหตุใหม่
        var json = OcrService.SerializeExtractedItems(d.Items.ToList());
        var again = new OcrExtractedData();
        foreach (var x in System.Text.Json.JsonSerializer.Deserialize<List<OcrExtractedLineItem>>(json)!) again.Items.Add(x);
        OcrService.SanitizeVatSplitArtifacts(again, false, null);
        var a = Assert.Single(again.Items);
        Assert.Equal((30.512m, 32.45m, 0.02m, 0.02m), (a.Quantity!.Value, a.UnitPrice!.Value, a.LineDiscountAmount!.Value, a.UnitPriceRoundingResidual!.Value));
        Assert.Empty(again.ReasoningTrace);
    }

    // ── P2-3: ตัดเฉพาะเครื่องหมายของตัวกัน ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Flash Drive (64 GB)", 1, "รายการ", "Flash Drive (64 GB)")]               // สเปกสินค้า — เดิมถูกตัดเหลือ "Flash Drive"
    [InlineData("Kingston DataTraveler (64 GB)", 1, "รายการ", "Kingston DataTraveler (64 GB)")]
    [InlineData("Monitor (27 นิ้ว)", 1, "รายการ", "Monitor (27 นิ้ว)")]
    [InlineData("ปูนซีเมนต์ 50 กก. (50 กก.)", 1, "รายการ", "ปูนซีเมนต์ 50 กก. (50 กก.)")]  // ตัวเลขในวงเล็บเป็นสเปกในคำอธิบายเอง
    [InlineData("แพ็กเกจอินเทอร์เน็ต (30 GB)", 1, "รายการ", "แพ็กเกจอินเทอร์เน็ต")]     // GB + บริบทดาต้า = ตัวกันเขียนได้
    [InlineData("ค่าไฟฟ้า ประจำเดือน 08/2569 (4,272 หน่วย)", 1, "รายการ", "ค่าไฟฟ้า ประจำเดือน 08/2569")]
    [InlineData("ค่าบริการ (จำนวน 4,272)", 1, "รายการ", "ค่าบริการ")]
    [InlineData("น้ำดื่ม 600ml (12 ขวด)", 1, "รายการ", "น้ำดื่ม 600ml")]                 // ท้ายรุ่น 6 (ทีม 8) — ย้อนหลัง
    [InlineData("คอนกรีตผสมเสร็จ 240 ksc (2.75 ลบ.ม.)", 1, "รายการ", "คอนกรีตผสมเสร็จ 240 ksc")]   // ท้ายรุ่น 7/8 — ย้อนหลัง
    [InlineData("Flash Drive (64 GB)", 3, "ชิ้น", "Flash Drive (64 GB)")]
    [InlineData("น้ำดื่ม 600ml (12 ขวด)", 1, "แพ็ค", "น้ำดื่ม 600ml (12 ขวด)")]
    public void ตัวตัดท้าย_ตัดเฉพาะเครื่องหมายของตัวกัน_สเปกสินค้าไม่ถูกตัด(string desc, double qty, string unit, string expected)
        => Assert.Equal(expected, OcrLineQuantityGuard.DescriptionForMatching(desc, (decimal)qty, unit));

    [Fact]
    public void ตัวตัดท้าย_สมมาตรกับตัวเขียน_ทุกท้ายที่ตัวกันเขียนวันนี้ตัดกลับได้()
    {
        foreach (var (desc, qty, unit, paper) in new (string, decimal, string?, string)[]
                 {
                     ("ค่าไฟฟ้า ประจำเดือน 08/2569", 4272m, null, "หน่วยที่ใช้ 4272.0019,381.50"),
                     ("ค่าน้ำประปา", 25m, null, "ค่าน้ำประปา 25 ลบ.ม. 246.13"),
                     ("ค่าบริการ", 4272m, null, "ค่าบริการ 4,272 หน่วย 19,381.50"),
                 })
        {
            var amount = desc == "ค่าน้ำประปา" ? 246.13m : 19381.50m;
            var seller = desc == "ค่าบริการ" ? OcrUtilityKind.Electricity : OcrUtilityKind.None;
            var d = OcrLineQuantityGuard.Decide(qty, null, amount, null, Paper(paper), desc, unit, seller);
            Assert.Equal(OcrLineQuantityAction.QuantityInDescription, d.Action);
            Assert.Equal(desc, OcrLineQuantityGuard.DescriptionForMatching(d.Description, 1m, d.Unit));
        }
    }
}
