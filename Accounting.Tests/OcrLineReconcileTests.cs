using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตรวจ SanitizeVatSplitArtifacts ขั้น "reconcile จำนวน" — กันเคสที่ OCR อ่านยอด
/// บรรทัด (Amount) ถูก แต่อ่านจำนวนผิด (มักอ่านตัวเลขคอลัมน์ยอดมาเป็นจำนวน) →
/// line-building คิด qty×unitPrice → ยอดพุ่งไกล. reconcile เชื่อ Amount เป็นหลัก
/// แก้ qty ให้ qty×price = Amount (ยอดบรรทัดถูกเสมอ).
/// </summary>
public class OcrLineReconcileTests
{
    private static OcrExtractedLineItem Line(decimal? qty, decimal? up, decimal? amt, string desc = "สินค้า A")
        => new() { Description = desc, Quantity = qty, UnitPrice = up, Amount = amt };

    private static OcrExtractedData WithLine(OcrExtractedLineItem l)
    {
        var d = new OcrExtractedData();
        d.Items.Add(l);
        return d;
    }

    [Fact]
    public void Qty_misread_as_amount_reconciles_to_line_total()
    {
        // OCR อ่านยอด 1,180 มาใส่เป็นจำนวน (qty=1180) → qty×price = 1.39M (ระเบิด)
        // Amount ที่อ่านถูก = 1,180 → ต้องแก้ qty=1 → line = 1,180
        var d = WithLine(Line(qty: 1180m, up: 1180m, amt: 1180m));
        OcrService.SanitizeVatSplitArtifacts(d);
        var it = d.Items[0];
        Assert.Equal(1m, it.Quantity);
        Assert.Equal(1180m, (it.UnitPrice ?? 0m) * (it.Quantity ?? 0m));
    }

    [Fact]
    public void Price_column_holding_the_line_total_is_divided_not_qty_reset()
    {
        // qty=10 (ข้อมูลอิสระ ไม่ใกล้ยอดเงิน), price=100 ≈ amount=100 —
        // ช่องราคาถืออยู่คือ "ยอดรวมบรรทัด" ไม่ใช่ราคาต่อหน่วย → หารหา
        // ราคาจริง (100/10=10) และ**เก็บจำนวนไว้** ไม่ทับเป็น 1
        // (พฤติกรรมเดิมทับ qty=1 — ทิ้งปริมาณที่อ่านได้จากกระดาษ; เปลี่ยน
        // ตามหลักบัญชี: ราคาทุนต่อหน่วย = ยอดจ่ายจริง ÷ ปริมาณ. ยอดบรรทัด
        // ที่ลง GL เท่ากันทั้งสองทาง = 100)
        // ★ ทีม OCR 8 (2026-10-16 · คำตัดสินเจ้าของ "ระบบต้องไม่แต่งราคาต่อหน่วยที่กระดาษไม่พิมพ์"): ไม่มีข้อความกระดาษให้พิสูจน์ว่าราคา 10 พิมพ์อยู่
        // ⇒ 1 × ยอด · จำนวน 10 อยู่ในคำอธิบาย (ไม่ทิ้งปริมาณ) · ยอดที่ลง GL = 100 เท่าเดิม · มีข้อความที่พิมพ์ราคา 10.00 ⇒ 10 × 10 (เดิม)
        // ★ ทีม OCR 9 (ฝ่ายค้าน 13a4d788 P0): "สินค้า A" ไม่มีหลักฐานมิเตอร์ และ 100 ÷ 10 = 10.00 ลงตัวทุกสตางค์ (เลขคณิต ไม่ใช่ราคาที่แต่ง) ⇒
        // คงจำนวน 10 × 10.00 (รุ่น 5 เดิม) · จำนวนไม่พิสูจน์บนกระดาษ ⇒ แท็กบล็อก (เดิม) · 1 × 100 ทำสต็อกขาด 9 ชิ้น
        var d = WithLine(Line(qty: 10m, up: 100m, amt: 100m));
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(10m, d.Items[0].Quantity);
        Assert.Equal(10m, d.Items[0].UnitPrice);
        Assert.Equal("สินค้า A", d.Items[0].Description);
        Assert.Contains(d.ReasoningTrace, t => t.StartsWith(Accounting.Helpers.OcrLineQuantityGuard.Tag + " ", StringComparison.Ordinal));
        Assert.Equal(100m, (d.Items[0].UnitPrice ?? 0m) * (d.Items[0].Quantity ?? 0m));

        var printed = WithLine(Line(qty: 10m, up: 100m, amt: 100m));
        OcrService.SanitizeVatSplitArtifacts(printed, false, "สินค้า A 10 ชิ้น 10.00 100.00");
        Assert.Equal(10m, printed.Items[0].Quantity);
        Assert.Equal(10m, printed.Items[0].UnitPrice);
        Assert.Equal("สินค้า A", printed.Items[0].Description);
    }

    [Fact]
    public void Real_pea_bill_keeps_the_meter_quantity_and_derives_the_kwh_price()
    {
        // เคสจริง (ผู้ใช้ส่งภาพมา): บิลค่าไฟ กฟภ. 3,611 หน่วย ยอดบรรทัด
        // 16,351.48 — OCR เอายอดรวมมาใส่ช่องราคา/หน่วย ⇒ ฟอร์มคิด
        // 3,611 × 16,351.48 = 59,046,338.88 (59 ล้าน). จำนวน 3,611 คือค่า
        // มิเตอร์จริง ทิ้งไม่ได้ → ราคา/หน่วย = 16,351.48 ÷ 3,611 ≈ 4.5282
        // ★ ทีม OCR 8 (2026-10-16): ราคา 4.5282 ไม่มีบนกระดาษ และ DocumentLine เก็บ 4.53 ⇒ บันทึกซ้ำ 16,357.83 ≠ 16,351.48 —
        // ตอนนี้ 1 × ยอด · จำนวนมิเตอร์ 3,611 อยู่ในคำอธิบาย (ทิ้งไม่ได้ — ยังเห็นบนเอกสาร) · ไม่มีข้อความกระดาษ ⇒ แท็กบล็อก (ยังไม่พิสูจน์จำนวน)
        var d = WithLine(Line(qty: 3611m, up: 16351.48m, amt: 16351.48m, desc: "ค่าไฟฟ้า ประจำเดือน 07/2569"));
        OcrService.SanitizeVatSplitArtifacts(d);
        var it = d.Items[0];
        Assert.Equal(1m, it.Quantity);
        Assert.Equal(16351.48m, it.UnitPrice);
        Assert.Equal("ค่าไฟฟ้า ประจำเดือน 07/2569 (3,611 หน่วย)", it.Description);
        Assert.Equal(16351.48m, Math.Round((it.UnitPrice ?? 0m) * (it.Quantity ?? 0m), 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Missing_unit_price_is_derived_from_amount_and_quantity()
    {
        // บิลสาธารณูปโภคพิมพ์แต่ปริมาณ+ยอดรวม ไม่พิมพ์ราคาต่อหน่วย —
        // เดิม skip เงียบ ๆ (ฟอร์มได้ช่องราคาว่าง) → หารหาให้เลย
        // ★ ทีม OCR 8: ไม่แต่ง 4.5282 (ไม่มีบนกระดาษ · เก็บ 2 ตำแหน่งไม่ได้) ⇒ 1 × ยอด + จำนวนในคำอธิบาย (แจ้ง ไม่บล็อก)
        // ★ ทีม OCR 9 (ฝ่ายค้าน 13a4d788 P0): "สินค้า A" ไม่มีหลักฐานมิเตอร์ (ไม่มีหน่วย/ป้าย/ผู้ขายสาธารณูปโภค) ⇒ ห้ามทิ้งจำนวน ⇒ คง 3,611 ·
        // ราคา round(16,351.48 ÷ 3,611, 2) = 4.53 · 3,611 × 4.53 = 16,357.83 · แท็กบล็อก
        // ★ ทีม OCR 11 (ฝ่ายค้าน OCR 9 P2-1 · ตั้งใจพลิก): เศษ 6.35 เกินเพดานปัด (1.00 บาท / 0.5%) ⇒ ไม่แต่ง "ส่วนลด" 6.35 อีก — ยอดบรรทัดตามกระดาษ ·
        //   จำนวน × ราคา ≠ ยอด โดยตั้งใจ · หมายเหตุให้ตรวจราคาต่อหน่วยบนกระดาษ
        var d = WithLine(Line(qty: 3611m, up: null, amt: 16351.48m));
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(3611m, d.Items[0].Quantity);
        Assert.Equal(4.53m, d.Items[0].UnitPrice);
        Assert.Null(d.Items[0].LineDiscountAmount);
        Assert.Null(d.Items[0].UnitPriceRoundingResidual);
        Assert.Equal(16351.48m, d.Items[0].Amount);
        Assert.Equal("สินค้า A", d.Items[0].Description);
        Assert.Contains(d.ReasoningTrace, t => t.StartsWith(Accounting.Helpers.OcrLineQuantityGuard.Tag + " ", StringComparison.Ordinal)
                                               && t.Contains("ไม่แต่งส่วนลด", StringComparison.Ordinal) && t.Contains("⟦3611×4.53⟧", StringComparison.Ordinal));
    }

    [Fact]
    public void Correct_line_untouched()
    {
        // qty×price = amount เป๊ะ → ไม่แตะ
        var d = WithLine(Line(qty: 3m, up: 100m, amt: 300m));
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(3m, d.Items[0].Quantity);
        Assert.Equal(100m, d.Items[0].UnitPrice);
    }

    [Fact]
    public void Within_tolerance_untouched()
    {
        // 3×100 = 300.00 vs Amount 300.02 → ต่าง ฿0.02 ≤ tol (฿1) → ไม่แตะ
        var d = WithLine(Line(qty: 3m, up: 100m, amt: 300.02m));
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(3m, d.Items[0].Quantity);
    }

    [Fact]
    public void Missing_price_or_amount_or_qty_skipped_safely()
    {
        // ทีม OCR 8: ไม่มีข้อความกระดาษพิสูจน์ราคา 100 ⇒ 1 × 500 · จำนวน 5 อยู่ในคำอธิบาย (ไม่หาย)
        // ★ ทีม OCR 9: 500 ÷ 5 = 100.00 ลงตัวทุกสตางค์ ⇒ คงจำนวน 5 × 100 (รุ่น 5 เดิม) · หมายเหตุแจ้ง (ไม่บล็อก)
        var noPrice = WithLine(Line(qty: 5m, up: null, amt: 500m));
        OcrService.SanitizeVatSplitArtifacts(noPrice);
        Assert.Equal(5m, noPrice.Items[0].Quantity);
        Assert.Equal(100m, noPrice.Items[0].UnitPrice);
        Assert.Equal("สินค้า A", noPrice.Items[0].Description);
        Assert.DoesNotContain(noPrice.ReasoningTrace, t => t.StartsWith(Accounting.Helpers.OcrLineQuantityGuard.Tag + " ", StringComparison.Ordinal));

        var noAmt = WithLine(Line(qty: 5m, up: 100m, amt: null));
        OcrService.SanitizeVatSplitArtifacts(noAmt);
        Assert.Equal(5m, noAmt.Items[0].Quantity);      // ไม่มี amount → skip
    }

    [Fact]
    public void Large_but_consistent_qty_kept()
    {
        // ขายจริง 100 หน่วย × 5 = 500 → qty×price ตรง amount → เก็บ qty=100 ไว้
        var d = WithLine(Line(qty: 100m, up: 5m, amt: 500m));
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(100m, d.Items[0].Quantity);
    }

    // ═══ T2-20: ยุบบรรทัดชื่อซ้ำโดยไม่ดูราคา = แต่งราคาต่อหน่วยที่ไม่มีบนกระดาษ ═══

    [Fact]
    public void ชื่อซ้ำแต่ราคาต่อหน่วยต่างกัน_ห้ามยุบเป็นบรรทัดเดียว()
    {
        // กระดาษ: "ค่าแรง 500" + "ค่าแรง 300" — เดิมยุบเป็น qty 2 × ฿400
        // ยอดรวมยังตรง (800) จึงเงียบสนิท แต่ ฿400 ไม่มีอยู่บนกระดาษเลย
        // และค่านี้ไหลต่อเป็นต้นทุนตอนนำเข้าสต๊อก + รายละเอียดรายบรรทัด §87
        var d = new OcrExtractedData();
        d.Items.Add(Line(qty: 1m, up: 500m, amt: 500m, desc: "ค่าแรง"));
        d.Items.Add(Line(qty: 1m, up: 300m, amt: 300m, desc: "ค่าแรง"));
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(2, d.Items.Count);
        Assert.Contains(d.Items, i => i.UnitPrice == 500m);
        Assert.Contains(d.Items, i => i.UnitPrice == 300m);
        Assert.Equal(800m, d.Items.Sum(i => i.Amount ?? 0m));
    }

    [Fact]
    public void ชื่อซ้ำและราคาต่อหน่วยเท่ากัน_ยังต้องยุบเหมือนเดิม()
    {
        // เจตนาเดิมของขั้นนี้: external OCR แตกสินค้าเดียวเป็น 2 บรรทัดเท่า ๆ กัน
        // (VAT split) — เคสนี้ต้องยังยุบได้ ไม่งั้นแก้บั๊กแล้วปิดฟีเจอร์ทิ้ง
        var d = new OcrExtractedData();
        d.Items.Add(Line(qty: 2m, up: 250m, amt: 500m, desc: "กระดาษ A4"));
        d.Items.Add(Line(qty: 1m, up: 250m, amt: 250m, desc: "กระดาษ A4"));
        OcrService.SanitizeVatSplitArtifacts(d);
        var only = Assert.Single(d.Items);
        Assert.Equal(3m, only.Quantity);
        Assert.Equal(750m, only.Amount);
        Assert.Equal(250m, only.UnitPrice);
    }
}
