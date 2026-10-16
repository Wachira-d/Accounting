using System.Reflection;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **บิลมิเตอร์ (เคส ก) — ห้ามแต่งราคาต่อหน่วยที่กระดาษไม่พิมพ์ · ห้ามเก็บบรรทัดที่ จำนวน × ราคา ≠ ยอด** (ทีม OCR 8 · 2026-10-16)
///
/// <para>ที่มา: ทีม OCR 6 พบว่าบิลค่าไฟ (จำนวนหน่วยมิเตอร์ + ยอด ไม่มีราคาต่อหน่วย) ได้ราคา = ยอด ÷ จำนวน ทศนิยม 4 (4.5282/หน่วย) แต่
/// <c>DocumentLine.UnitPrice</c> เก็บ 2 ตำแหน่ง ⇒ 4.53 × 3,611 = 16,357.83 ≠ กระดาษ 16,351.48 — เปิดแก้แล้วบันทึกเลื่อน 6.35 ·
/// เจ้าของเปิดบิลจริง: "ลองเปิดดูแล้วมี 2 หน่วยนะ 4หน่วยน่าจะเป็นค่า ft มากกว่า" ⇒ กระดาษพิมพ์แต่ยอด 2 ตำแหน่ง ตัวเลข 4 ตำแหน่งตัวเดียว
/// คืออัตราค่า Ft (บาท/หน่วย) — ไม่มีราคาต่อหน่วยบนกระดาษ ระบบห้ามแต่ง (F2 ข้อ 3) · ห้ามขยาย precision ของ UnitPrice (เจ้าของ: ไม่จำเป็น)</para>
///
/// <para>รูปที่ตัดสิน: <b>1 × ยอด (2 ตำแหน่ง ลงตัวทุกสตางค์) · จำนวนหน่วยอยู่ในคำอธิบาย</b> "ค่าไฟฟ้า ประจำเดือน 08/2569 (4,272 หน่วย)" ·
/// ตัวตัดสินตัวเดียว <see cref="OcrLineQuantityGuard.Decide"/> (<see cref="OcrLineQuantityAction.QuantityInDescription"/>) · ไม่แยกค่าไฟฐาน/ค่า Ft
/// (เหตุผลใน docs/lessons/ocr-pipeline.md 2026-10-16 ทีม OCR 8)</para>
/// </summary>
public class OcrMeterBillLumpSumTests
{
    private const string PeaDesc = "ค่าไฟฟ้า ประจำเดือน 08/2569";
    private const string PeaDescAfter = "ค่าไฟฟ้า ประจำเดือน 08/2569 (4,272 หน่วย)";

    private static DocumentLineRequest L(decimal q, decimal up, decimal vatRate = 7m)
        => new(Description: "x", Quantity: q, Unit: "รายการ", UnitPrice: up, DiscountPercent: 0m, VatRate: vatRate,
            WithholdingTaxRate: 0m, AccountId: null, DiscountAmount: 0m);

    private static OcrExtractedData PeaScan(decimal? qty, decimal? unitPrice)
    {
        var d = new OcrExtractedData { SubTotal = 19381.50m, VatAmount = 1356.71m, TotalAmount = 20738.21m };
        d.Items.Add(new OcrExtractedLineItem { Description = PeaDesc, Quantity = qty, UnitPrice = unitPrice, Amount = 19381.50m });
        return d;
    }

    // ── ใบจริง: ทำซ้ำก่อนแก้ + หลังแก้ ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ใบจริงPEA_ก่อนแก้_ราคา4ตำแหน่ง4_5369_เก็บ4_54_บันทึกซ้ำ19394_88_ไม่ตรงกระดาษ()
    {
        // ตัวเลขของบั๊ก (ยืนยันว่าแก้ถูกตัว — CLAUDE #4 G): 19,381.50 ÷ 4,272 = 4.5369 · DocumentLine.UnitPrice numeric(18,2) ⇒ 4.54
        Assert.Equal(4.5369m, Math.Round(19381.50m / 4272m, 4, MidpointRounding.AwayFromZero));
        var drift = DocumentService.PreviewTotals(new[] { L(4272m, 4.54m) }, false, 0m);
        Assert.Equal(19394.88m, drift.Net);       // +13.38 จากกระดาษ
        Assert.NotEqual(20738.21m, drift.Total);
    }

    [Theory]
    [InlineData(null)]            // engine คืนจำนวน + ยอด ไม่มีราคา (ตรงกระดาษ)
    [InlineData(19381.50)]        // engine อ่านยอดมาใส่ช่องราคา (เคส ก เดิม)
    public void ใบจริงPEA_Sanitize_ลง1คูณยอด_4272หน่วยในคำอธิบาย_หน่วยรายการ_แจ้งไม่บล็อก(double? enginePrice)
    {
        var d = PeaScan(4272m, enginePrice is double p ? (decimal)p : null);
        OcrService.SanitizeVatSplitArtifacts(d, false, OcrPaperSamples.PeaElectricityBill0869);
        var it = Assert.Single(d.Items);
        Assert.Equal(1m, it.Quantity);
        Assert.Equal(19381.50m, it.UnitPrice);
        Assert.Equal(19381.50m, it.Amount);
        Assert.Null(it.LineDiscountAmount);
        Assert.Equal(PeaDescAfter, it.Description);
        Assert.Equal(OcrLineQuantityGuard.LumpSumUnit, it.Unit);
        // หมายเหตุแจ้ง (ไม่บล็อก) ถึง ProcessingNotes ผ่านตัวต่อตัวเดียว · อนุมัติเองได้
        var note = Assert.Single(d.ReasoningTrace, t => t.StartsWith(OcrLineQuantityGuard.NoteTag, StringComparison.Ordinal));
        Assert.Contains(OcrLineQuantityGuard.QuantityInDescriptionPhrase, note);
        Assert.DoesNotContain(d.ReasoningTrace, t => t.StartsWith(OcrLineQuantityGuard.Tag + " ", StringComparison.Ordinal));
        var notes = OcrLineQuantityGuard.MergeNotes(null, d.ReasoningTrace);
        Assert.True(OcrPostingReadiness.Evaluate(notes, hasUsableDate: true).CanAutoApprove);
    }

    [Fact]
    public void ใบจริงPEA_สร้าง_เปิด_บันทึก_ยอดไม่เลื่อน_และSanitizeซ้ำบนJSONไม่เปลี่ยนอะไร()
    {
        var d = PeaScan(4272m, null);
        OcrService.SanitizeVatSplitArtifacts(d, false, OcrPaperSamples.PeaElectricityBill0869);
        // สร้างเอกสาร: เส้นสร้าง/พรีวิว/ดึงรายการซ้ำ อ่าน ExtractedItemsJson แล้วรัน Sanitize อีกรอบ (มี/ไม่มีข้อความกระดาษ) ⇒ ค่าเดิม ไม่ต่อวงเล็บซ้ำ
        var json = OcrService.SerializeExtractedItems(d.Items.ToList());
        foreach (var text in new[] { OcrPaperSamples.PeaElectricityBill0869, null })
        {
            var again = new OcrExtractedData();
            foreach (var it in System.Text.Json.JsonSerializer.Deserialize<List<OcrExtractedLineItem>>(json)!) again.Items.Add(it);
            OcrService.SanitizeVatSplitArtifacts(again, false, text);
            var a = Assert.Single(again.Items);
            Assert.Equal((1m, 19381.50m, 19381.50m), (a.Quantity!.Value, a.UnitPrice!.Value, a.Amount!.Value));
            Assert.Equal(PeaDescAfter, a.Description);
            Assert.Empty(again.ReasoningTrace);
        }
        // เปิดแก้แล้วบันทึก = ComputeLineAmounts ของ DocumentService (สูตรจริง) — สองรอบ ยอดเดิมทุกสตางค์
        var line = d.Items[0];
        var first = DocumentService.PreviewTotals(new[] { L(line.Quantity!.Value, line.UnitPrice!.Value) }, false, 0m);
        Assert.Equal((19381.50m, 1356.71m, 20738.21m), (first.Net, first.Vat, first.Total));
        var resaved = DocumentService.PreviewTotals(new[] { L(1m, Math.Round(line.UnitPrice.Value, 2, MidpointRounding.AwayFromZero)) }, false, 0m);
        Assert.Equal(first, resaved);
    }

    [Fact]
    public void ใบจริงPEA_ตัวสร้างบรรทัดของharness_ยอดVATรวมตรงกระดาษ_ผลต่างปัดเศษศูนย์_บันทึกซ้ำได้เท่าเดิม()
    {
        var paper = OcrReplayHarness.Corpus.Single(p => p.Name == "pea-electricity-bill-0869");
        var built = OcrReplayHarness.BuildLines(paper, 19381.50m, 1356.71m, 20738.21m, 0m, PaperWhtReader.Read(paper.RawText));
        var c = built.Lines.Split('|').Select(x => decimal.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(0m, built.RoundingAdjustment);
        var t = DocumentService.PreviewTotals(new[] { L(1m, c[3], c[1]) }, false, 0m);
        Assert.Equal((19381.50m, 1356.71m, 20738.21m), (t.Net, t.Vat, t.Total));
    }

    [Fact]
    public void สแกนเก่าที่persistราคา4ตำแหน่ง_สร้างเอกสารใหม่ได้1คูณยอด_ไม่เลื่อน()
    {
        // ExtractedItemsJson ของสแกนก่อนรอบนี้ถือ (4,272 × 4.5369) — ลงตัวในเกณฑ์ ±2% จึงเคยผ่านเป็น Keep ⇒ DocumentLine เก็บ 4.54 ⇒ เลื่อน
        var d = PeaScan(4272m, 4.5369m);
        OcrService.SanitizeVatSplitArtifacts(d, false, OcrPaperSamples.PeaElectricityBill0869);
        var it = Assert.Single(d.Items);
        Assert.Equal((1m, 19381.50m), (it.Quantity!.Value, it.UnitPrice!.Value));
        Assert.Equal(PeaDescAfter, it.Description);   // 4.5369 ไม่ได้พิมพ์บนกระดาษ ⇒ ไม่ใส่ "× 4.5369"
        Assert.Single(d.ReasoningTrace, t => t.StartsWith(OcrLineQuantityGuard.NoteTag, StringComparison.Ordinal));
        // ราคา 4 ตำแหน่งที่กระดาษพิมพ์จริง (น้ำมัน): ทีม OCR 8 ⇒ 1 × ยอด "(45.123 ลิตร × 32.4567)" ·
        // ★ ทีม OCR 9 (ฝ่ายค้าน 13a4d788 P0): ลิตรน้ำมันเป็นสินค้า (เข้าสต็อกได้) ไม่ใช่มิเตอร์ ⇒ คงจำนวน 45.123 · ราคา round(1,464.52 ÷ 45.123, 2) = 32.46 ·
        //   45.123 × 32.46 = 1,464.69 ⇒ ส่วนลดบรรทัด 0.17 (ยอดตรงกระดาษ · บันทึกซ้ำไม่เลื่อน) · แท็กบล็อก (ระบบเปลี่ยนราคาที่พิมพ์)
        var fuel = OcrLineQuantityGuard.Decide(45.123m, 32.4567m, 1464.52m, null,
            OcrLineQuantityGuard.Read("น้ำมันดีเซล 45.123 ลิตร 32.4567 1,464.52"), "น้ำมันดีเซล");
        Assert.Equal(OcrLineQuantityAction.RoundedUnitPrice, fuel.Action);
        Assert.Null(fuel.Description);
        Assert.Null(fuel.Quantity);
        Assert.Equal((32.46m, 0.17m), (fuel.UnitPrice!.Value, fuel.LineDiscount!.Value));
        Assert.True(fuel.Blocking);
        Assert.Contains("ราคาที่อ่านได้ 32.4567", fuel.Note);
        Assert.Equal(1464.52m, DocumentService.PreviewTotals(new[] { new DocumentLineRequest(Description: "x", Quantity: 45.123m, Unit: "ลิตร",
            UnitPrice: 32.46m, DiscountPercent: 0m, VatRate: 7m, WithholdingTaxRate: 0m, AccountId: null, DiscountAmount: 0.17m) }, false, 0m).Net);
    }

    [Fact]
    public void ใบจริงPEA_จำนวนหน่วยพิสูจน์จากป้ายหน่วยที่ใช้_ก่อนตัวเลข_ไม่มีป้าย_บล็อก()
    {
        // ข้อความ PdfPig ของใบนี้: "หน่วยที่ใช้ 4272.0019,381.50…" — ไม่มีคำหน่วยนับตามหลังตัวเลข แต่มีป้ายก่อน ⇒ พิสูจน์แล้ว (แจ้ง)
        var proven = OcrLineQuantityGuard.Decide(4272m, 19381.50m, 19381.50m, null,
            OcrLineQuantityGuard.Read(OcrPaperSamples.PeaElectricityBill0869), PeaDesc);
        Assert.False(proven.Blocking);
        // ทิศตรงข้าม: ตัวเลข 4,272 ที่ไม่มีป้าย/หน่วยนับคู่ ⇒ ยังไม่พิสูจน์ (บล็อก) แต่บรรทัดยังลงตัว 1 × ยอด
        var bare = OcrLineQuantityGuard.Decide(4272m, 19381.50m, 19381.50m, null,
            OcrLineQuantityGuard.Read("ค่าไฟฟ้า 4,272.00 19,381.50"), PeaDesc);
        Assert.True(bare.Blocking);
        Assert.Equal((1m, 19381.50m), (bare.Quantity!.Value, bare.UnitPrice!.Value));
        Assert.Equal(PeaDescAfter, bare.Description);
        Assert.Contains("⟦1×19381.5⟧", bare.Note);
    }

    // ── ทางเข้าอื่นของบรรทัดมิเตอร์/ชั่ง: น้ำ · โทรศัพท์/เน็ต · ชั่งน้ำหนัก ──────────────────────────────────────────

    [Theory]
    // ★ ทีม OCR 9: ค่าที่ไม่ลงตัว (ยอด ÷ จำนวน ปัด 2 ตำแหน่งแล้ว × จำนวน ≠ ยอด) — ค่าที่ลงตัวพอดีอยู่ในเทสต์ถัดไป (คงจำนวน)
    [InlineData("ค่าน้ำประปา ประจำเดือน 08/2569", 25, 246.13, null, "ค่าน้ำประปา ประจำเดือน 08/2569 (25 ลบ.ม.)")]
    [InlineData("ค่าบริการอินเทอร์เน็ต", 3, 1798.00, null, "ค่าบริการอินเทอร์เน็ต (3 เดือน)")]
    [InlineData("ค่าโทรศัพท์ทางไกล", 37, 100.00, "ครั้ง", "ค่าโทรศัพท์ทางไกล (37 ครั้ง)")]
    public void บรรทัดมิเตอร์มีจำนวนและยอด_ไม่มีราคาบนกระดาษ_ไม่ลงตัว_ลง1คูณยอด_จำนวนในคำอธิบาย(
        string desc, double qty, double amount, string? unit, string expected)
    {
        var d = OcrLineQuantityGuard.Decide((decimal)qty, null, (decimal)amount, null, OcrLineQuantityGuard.Read($"{desc} {amount:N2}"), desc, unit);
        Assert.Equal(OcrLineQuantityAction.QuantityInDescription, d.Action);
        Assert.Equal((1m, (decimal)amount), (d.Quantity!.Value, d.UnitPrice!.Value));
        Assert.Equal(expected, d.Description);
        Assert.False(d.Blocking);
        Assert.Contains("ตามมิเตอร์", d.Note);
        Assert.Equal((decimal)amount, DocumentService.PreviewTotals(new[] { L(1m, d.UnitPrice.Value, 0m) }, false, 0m).Net);
    }

    [Theory]
    // ทีม OCR 8 ลงทั้งสี่บรรทัดนี้เป็น 1 × ยอด · ★ ทีม OCR 9 (ฝ่ายค้าน 13a4d788 P0): ยอด ÷ จำนวน ลงตัวทุกสตางค์ ⇒ คงจำนวน + ราคา 2 ตำแหน่ง (รุ่น 5 เดิม) ·
    // หมายเหตุแจ้งที่ไม่พูดถึงมิเตอร์ · คำอธิบายไม่ถูกต่อ
    [InlineData("ค่าน้ำประปา ประจำเดือน 08/2569", 25, 245.00, null, 9.80)]
    [InlineData("ค่าบริการอินเทอร์เน็ต", 2, 1180.00, null, 590.00)]
    [InlineData("หมูสับ", 0.45, 54.00, "กก.", 120.00)]
    [InlineData("ลวดทองแดง", 0.35, 100.00, null, 285.71)]
    public void บรรทัดมีจำนวนและยอด_ไม่มีราคาบนกระดาษ_ยอดหารจำนวนลงตัว_คงจำนวน_ราคา2ตำแหน่ง_แจ้ง(
        string desc, double qty, double amount, string? unit, double price)
    {
        var d = OcrLineQuantityGuard.Decide((decimal)qty, null, (decimal)amount, null, OcrLineQuantityGuard.Read($"{desc} {amount:N2}"), desc, unit);
        Assert.Equal(OcrLineQuantityAction.FillUnitPrice, d.Action);
        Assert.Null(d.Quantity);
        Assert.Equal((decimal)price, d.UnitPrice);
        Assert.Null(d.Description);
        Assert.False(d.Blocking);
        Assert.DoesNotContain("มิเตอร์", d.Note);
        Assert.Equal((decimal)amount, DocumentService.PreviewTotals(new[] { L((decimal)qty, d.UnitPrice!.Value, 0m) }, false, 0m).Net);
    }

    [Fact]
    public void ทางเข้าAIแตกบรรทัด_โมเดลไม่ส่งราคา_ตัวกันตัดสินจากกระดาษ_ไม่เติม4ตำแหน่ง()
    {
        var split = OcrLineSplitGuard.Evaluate("""{"lines":[{"description":"ค่าไฟฟ้า ประจำเดือน 08/2569","quantity":4272,"amount":19381.50}]}""",
            subTotal: 19381.50m, totalAmount: 20738.21m);
        Assert.True(split.Accepted);
        Assert.Null(split.Lines[0].UnitPrice);   // เดิม 4.5369
        var d = new OcrExtractedData();
        d.Items.Add(new OcrExtractedLineItem
            { Description = split.Lines[0].Description, Quantity = split.Lines[0].Quantity, UnitPrice = split.Lines[0].UnitPrice, Amount = split.Lines[0].Amount });
        OcrService.SanitizeVatSplitArtifacts(d, false, OcrPaperSamples.PeaElectricityBill0869);
        Assert.Equal((1m, 19381.50m), (d.Items[0].Quantity!.Value, d.Items[0].UnitPrice!.Value));
        Assert.Equal(PeaDescAfter, d.Items[0].Description);
    }

    // ── ทิศตรงข้าม: ราคาต่อหน่วยที่กระดาษพิมพ์ 2 ตำแหน่ง ไม่ถูกแตะ ───────────────────────────────────────────────

    [Theory]
    [InlineData(2, 268.00, 536.00, "แพ็คคู่ 2 268.00 536.00")]              // Shopee
    [InlineData(4, 1228.04, 4912.15, "นมผง 4 1,228.04 4,912.15")]          // Lazada เศษ 0.01 (ผลต่างปัดเศษหัวเอกสาร)
    [InlineData(45.123, 32.45, 1464.24, "ดีเซล 45.123 ลิตร 32.45 1,464.24")] // จำนวน 3 ตำแหน่ง × ราคา 2 ตำแหน่ง
    [InlineData(3, 100.00, 300.02, "สินค้า 3 100.00 300.02")]              // ต่างในเกณฑ์ ±2% ราคา 2 ตำแหน่ง ⇒ คงเดิม
    public void ราคาต่อหน่วยที่พิมพ์2ตำแหน่ง_คงจำนวนราคาตามกระดาษ_ไม่ย้ายไปคำอธิบาย(double qty, double price, double amount, string text)
    {
        var d = OcrLineQuantityGuard.Decide((decimal)qty, (decimal)price, (decimal)amount, null, OcrLineQuantityGuard.Read(text), "x");
        Assert.Equal(OcrLineQuantityAction.Keep, d.Action);
        Assert.Null(d.Description);
        Assert.Null(d.Quantity);
        Assert.Null(d.UnitPrice);
    }

    [Fact]
    public void ผู้ใช้แก้บรรทัดเอง_ไม่ย้ายจำนวนไปคำอธิบาย_ค่าผู้ใช้ชนะ()
    {
        var d = new OcrExtractedData();
        d.Items.Add(new OcrExtractedLineItem { Description = PeaDesc, Quantity = 4272m, UnitPrice = 4.54m, Amount = 19394.88m, UserEdited = true });
        OcrService.SanitizeVatSplitArtifacts(d, false, OcrPaperSamples.PeaElectricityBill0869);
        Assert.Equal((4272m, 4.54m), (d.Items[0].Quantity!.Value, d.Items[0].UnitPrice!.Value));
        Assert.Equal(PeaDesc, d.Items[0].Description);
    }

    [Fact]
    public void ตัวประกอบคำอธิบาย_ไม่ต่อวงเล็บซ้ำ_คำอธิบายว่างได้วงเล็บอย่างเดียว()
    {
        Assert.Equal(PeaDescAfter, OcrLineQuantityGuard.QuantityDescription(PeaDescAfter, 4272m, null));
        // ทีม OCR 9: "หน่วย" เป็นหน่วยมิเตอร์เฉพาะเมื่อคำอธิบายบอก "ค่าไฟ" (ไม่มีคำอธิบาย ⇒ "จำนวน X" — รูปท้ายเดียวที่ตัวตัดกลับรู้จัก)
        Assert.Equal("(จำนวน 4,272)", OcrLineQuantityGuard.QuantityDescription(null, 4272m, "หน่วย"));
        Assert.Equal(PeaDescAfter, OcrLineQuantityGuard.QuantityDescription(PeaDesc, 4272m, OcrLineQuantityGuard.LumpSumUnit));
        // "ชิ้น" ค่าตั้งต้นที่หลุดมาบนบรรทัดค่าไฟ ⇒ หน่วยที่อนุมาน · หน่วยอื่นที่ engine ส่ง = ตามที่ส่ง · ไม่รู้หน่วย = "จำนวน X"
        Assert.Equal(PeaDescAfter, OcrLineQuantityGuard.QuantityDescription(PeaDesc, 4272m, "ชิ้น"));
        Assert.Equal("ค่าไฟฟ้า (4,272 kWh)", OcrLineQuantityGuard.QuantityDescription("ค่าไฟฟ้า", 4272m, "kWh"));
        // ทีม OCR 9: หน่วยสินค้า ("ชิ้น" ค่าตั้งต้น) ไม่ใช่หน่วยมิเตอร์ ⇒ "จำนวน X"
        Assert.Equal("ลวด (จำนวน 2)", OcrLineQuantityGuard.QuantityDescription("ลวด", 2m, "ชิ้น"));
    }

    // ── หัวใบของบิลนี้บนเส้นข้อความ PDF ล้วน (Tier 3 hybrid ไม่มี Tesseract) — ล็อก "คำตอบวันนี้" (เจอแต่ไม่แก้ · รายงานแล้ว) ─────────────

    [Fact]
    public void ใบจริงPEA_เส้นข้อความPDF_หัวใบที่ถูก_และที่อ่านผิดล็อกไว้ให้เห็นเมื่อมีคนแก้()
    {
        var text = OcrPaperSamples.PeaElectricityBill0869;
        var parse = typeof(OcrService).GetMethod("ParseThaiDocument", BindingFlags.NonPublic | BindingFlags.Static)!;
        var data = (OcrExtractedData)parse.Invoke(null, new object[] { ThaiTextNormalizer.Normalize(text) })!;
        SmartFieldExtractor.Enrich(data, text);
        // ✓ ถูก: เลขผู้เสียภาษีผู้ขาย · วันที่ พ.ศ. 11/09/2569 ⇒ 2026-09-11 (ตัวตรวจวันที่กับกระดาษยืนยัน)
        Assert.Equal("0994000165501", data.VendorTaxId);
        var date = OcrDateReader.CrossCheck(data.DocumentDate, ThaiTextNormalizer.Normalize(text), new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 9, 11), date.Date);
        // ⚠️ เจอแต่ไม่แก้ (ข้อความ PdfPig เรียงป้ายกับค่าสลับ · ช่องติดกัน — ต้องอ่านพร้อมพิกัด ไม่ใช่แก้เล็ก):
        //   เลขที่เอกสาร = 516004632612 (เลขที่ใบแจ้งหนี้) — บนกระดาษ "เลขที่ (No.)" ของใบกำกับคือ XH0712609004157 ·
        //   สาขาผู้ขายว่าง (กระดาษพิมพ์ 00000 แต่ข้อความต่อกับรหัสไปรษณีย์ "1090000000") ·
        //   ยอด 689.92/48.29/738.21 (กระดาษ 19,381.50/1,356.71/20,738.21 — "1,356.7120,738.21" ติดกัน · [AmountWords] เตือนแล้ว) ·
        //   ไม่มีรายการ (ตารางไม่แยกช่อง) ⇒ เส้นนี้ได้บรรทัดสรุปใบเดียว
        Assert.Equal("516004632612", data.DocumentNumber);
        // ทีม OCR 10 (2026-10-16): ตัวอ่านสาขารับป้ายสองภาษา "สาขา (Branch No.) 00000" แล้ว ⇒ ข้อความเดิมได้ 00000 จากบรรทัด<b>ผู้ซื้อ</b>
        // (ป้ายผู้ซื้อตัวเดียวคือ "รหัสลูกค้า" ซึ่งพิมพ์หลังบรรทัดนั้น) — ค่าตรงกระดาษโดยบังเอิญ (ผู้ขายก็ 00000) ไม่ใช่การอ่านที่ถูก ·
        // ข้อความใหม่ (PeaElectricityBill0869Positional) ได้ 00000 จากประโยค "สาขาที่ออกใบกำกับภาษี" (ล็อกใน OcrPdfTextPathTests)
        // ทีม OCR 13 (ฝ่ายค้าน OCR 10 P2 · F2 #3): ค่าที่ "ถูกโดยบังเอิญ" ไม่ใช่การอ่าน — สาขาที่มาจากบรรทัดเลขผู้ซื้อ (กุญแจ = เลข 0203562005871) ถูกล้าง
        // (BranchCodeExtractor.VendorBranchFromBuyerLine) ⇒ ว่าง ให้ขั้นทะเบียน/ประวัติผู้ขาย/ผู้ใช้เติม · ไม่รู้ ดีกว่าเดาถูกเพราะโชค
        Assert.Null(data.VendorBranchCode);
        Assert.Contains(data.ReasoningTrace, t => t.Contains("บรรทัดเลขผู้เสียภาษีผู้ซื้อ", StringComparison.Ordinal));
        Assert.Equal(738.21m, data.TotalAmount);
        Assert.Contains(data.ReasoningTrace, t => t.Contains("[AmountWords]", StringComparison.Ordinal) && t.Contains("20,738.21", StringComparison.Ordinal));
        Assert.Empty(data.Items);
    }
}
