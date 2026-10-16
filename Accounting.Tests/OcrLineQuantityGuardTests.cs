using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ตัวกันจำนวนระเบิด + หลักฐานจากแถวบนกระดาษ** (<see cref="OcrLineQuantityGuard"/> · 2026-10-16 · ผู้ใช้รายงาน RE-20261000111 พรามี่ ควอลิตี้)
///
/// <para>"จำนวนสินค้า ทำไมระบบเปลี่ยนไปจากเอกสาร ในเอกสารขึ้นเป็นส่วนลดตามรายการ ระบบต้องจัดการบันทึกให้ถูกส่วน ไม่ใช่มาปรับจำนวนรายการ" —
/// กระดาษ 1.00 ชิ้น × 1,330.00 − ส่วนลด 293.00 = 1,037.00 (รวม VAT) · engine คืน (1, 1,330, 1,037) ไม่มีช่องส่วนลด ⇒ ตัวกันเดิม
/// (<c>OcrService.SanitizeVatSplitArtifacts</c> ข้อ 1b) หาร 1,037 ÷ 1,330 = <b>0.780</b> (ทำซ้ำได้ในเทสต์ <c>ไม่มีข้อความกระดาษ_…</c>)</para>
///
/// <para>ยอด "หลังบันทึก" คิดผ่าน <see cref="DocumentService.PreviewTotals"/> (ComputeLineAmounts + ReconcileTaxRounding ตัวจริง) ไม่ใช่สูตรที่คัดลอก ·
/// ทิศตรงข้าม (จำนวนไม่ได้พิมพ์ / engine อ่านจำนวนผิด / อ่านยอดเป็นจำนวน / มิเตอร์) ล็อกไว้ในไฟล์เดียวกัน</para>
/// </summary>
public class OcrLineQuantityGuardTests
{
    private static OcrLinePaper Paper(string text) => OcrLineQuantityGuard.Read(text);

    /// <summary>ข้อความแบบ engine ภาพ (Azure/Tesseract — ช่องคั่นด้วยช่องว่าง/ขึ้นบรรทัด) ของแถวเดียวกันบนใบ RE-20261000111</summary>
    private const string PramieOcrLines =
        "คำอธิบาย จำนวน หน่วย ราคา ส่วนลด VAT มูลค่ารวมภาษี\n" +
        "1. (แพ็ค3)แผ่นรองซับ M 50 แผ่น (P00022)\n(แพ็ค3)แผ่นรองซับ M 50 แผ่น\n1.00\nชิ้น\n1,330.00\n293.00\n7%\n1,037.00\n" +
        "มูลค่าที่คำนวณภาษี 7% 969.16 บาท\nภาษีมูลค่าเพิ่ม 7% 67.84 บาท\nจำนวนเงินทั้งสิ้น 1,037.00 บาท";

    // ── ใบจริง RE-20261000111 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ใบจริงRE111_ข้อความPdfPig_ส่วนลด293อยู่บนแถว_ลงเป็นส่วนลดบรรทัด_จำนวนราคาคงตามกระดาษ_หมายเหตุแจ้งไม่บล็อก()
    {
        var d = OcrLineQuantityGuard.Decide(1m, 1330m, 1037m, null, Paper(OcrPaperSamples.PramieLineDiscountPdfText));
        Assert.Equal(OcrLineQuantityAction.PrintedDiscount, d.Action);
        Assert.Equal(293.00m, d.LineDiscount);
        Assert.Null(d.Quantity);    // ไม่เปลี่ยนจำนวน
        Assert.Null(d.UnitPrice);   // ไม่เปลี่ยนราคา
        // ฝ่ายค้าน 658e3705 P1-1 ข้อ d: ทุกส่วนลดจากกระดาษต้องบอกคน (แจ้ง ไม่บล็อก) — หลักฐานคือหัวคอลัมน์ "ส่วนลด" (PUA ของฟอนต์ถูกแปลงก่อนค้นคำ)
        Assert.StartsWith(OcrLineQuantityGuard.NoteTag, d.Note);
        Assert.Contains("ส่วนลดรายบรรทัด 293.00 จากกระดาษ", d.Note);
        Assert.False(d.Blocking);
        Assert.True(OcrPostingReadiness.Evaluate(d.Note, hasUsableDate: true).CanAutoApprove);
        Assert.True(Paper(OcrPaperSamples.PramieLineDiscountPdfText).HeaderHasDiscount);
    }

    [Fact]
    public void ใบจริงRE111_ข้อความแบบengineภาพ_แยกช่องด้วยช่องว่างและขึ้นบรรทัด_ได้ส่วนลด293เหมือนกัน()
    {
        var d = OcrLineQuantityGuard.Decide(1m, 1330m, 1037m, null, Paper(PramieOcrLines));
        Assert.Equal(OcrLineQuantityAction.PrintedDiscount, d.Action);
        Assert.Equal(293.00m, d.LineDiscount);
    }

    [Fact]
    public void ไม่มีข้อความกระดาษ_สูตรเดิมหารจำนวน0_78_แต่ไม่เงียบ_มีแท็กLINEQTY()
    {
        // ทำซ้ำค่าที่ผู้ใช้เห็นบนใบสำคัญจ่าย (จำนวน 0.78) — ทางนี้เหลือเฉพาะเมื่อไม่มีหลักฐานบนกระดาษเลย และตอนนี้ห้ามอนุมัติเอง
        var d = OcrLineQuantityGuard.Decide(1m, 1330m, 1037m, null, null);
        Assert.Equal(OcrLineQuantityAction.FixQuantity, d.Action);
        Assert.Equal(0.780m, d.Quantity);
        Assert.StartsWith(OcrLineQuantityGuard.Tag, d.Note);
        Assert.False(OcrPostingReadiness.Evaluate(d.Note, hasUsableDate: true).CanAutoApprove);
    }

    [Fact]
    public void Sanitize_ใบจริงRE111_จำนวน1_ราคา1330_ส่วนลด293_ธงรวมVAT_ไม่มีแท็ก_สร้างซ้ำไม่เปลี่ยน()
    {
        var data = new OcrExtractedData { SubTotal = 969.16m, VatAmount = 67.84m, TotalAmount = 1037.00m };
        data.Items.Add(new OcrExtractedLineItem { Description = "(แพ็ค3)แผ่นรองซับ M 50 แผ่น (P00022)", Quantity = 1m, UnitPrice = 1330m, Amount = 1037m });
        OcrService.SanitizeVatSplitArtifacts(data, false, OcrPaperSamples.PramieLineDiscountPdfText);
        var it = Assert.Single(data.Items);
        Assert.Equal(1m, it.Quantity);
        Assert.Equal(1330m, it.UnitPrice);
        Assert.Equal(1037m, it.Amount);
        Assert.Equal(293m, it.LineDiscountAmount);
        Assert.True(it.PriceIncludesVat);   // Σ 1,037 = ยอดรวม ≠ ฐาน 969.16 ⇒ ราคา/ส่วนลดบนแถวรวม VAT (เคส A ของ OcrLineReconciler)
        Assert.DoesNotContain(data.ReasoningTrace, t => t.StartsWith(OcrLineQuantityGuard.Tag, StringComparison.Ordinal));

        // เส้นสร้างเอกสาร (สร้างซ้ำ/แกะบรรทัดใหม่) รัน Sanitize อีกรอบบนสำเนาจาก JSON ที่ไม่มีหัวใบ — ทั้งมีและไม่มีข้อความ ⇒ ค่าเดิม (ส่วนลดพิสูจน์แล้ว)
        foreach (var text in new[] { OcrPaperSamples.PramieLineDiscountPdfText, null })
        {
            var again = new OcrExtractedData();
            again.Items.Add(it);
            OcrService.SanitizeVatSplitArtifacts(again, false, text);
            Assert.Equal(1m, again.Items[0].Quantity);
            Assert.Equal(293m, again.Items[0].LineDiscountAmount);
            Assert.Empty(again.ReasoningTrace);
        }
    }

    [Fact]
    public void Sanitize_ไม่ส่งข้อความกระดาษ_พฤติกรรมเดิม0_78_แต่ReasoningTraceมีแท็ก()
    {
        var data = new OcrExtractedData { SubTotal = 969.16m, VatAmount = 67.84m, TotalAmount = 1037.00m };
        data.Items.Add(new OcrExtractedLineItem { Description = "x", Quantity = 1m, UnitPrice = 1330m, Amount = 1037m });
        OcrService.SanitizeVatSplitArtifacts(data);
        Assert.Equal(0.780m, data.Items[0].Quantity);
        Assert.Null(data.Items[0].LineDiscountAmount);
        Assert.False(data.Items[0].PriceIncludesVat);
        Assert.Contains(data.ReasoningTrace, t => t.StartsWith(OcrLineQuantityGuard.Tag, StringComparison.Ordinal));
    }

    [Fact]
    public void บันทึกซ้ำด้วยตัวคำนวณเอกสาร_ฐาน969_16_VAT67_84_รวม1037_ทุกสตางค์_ค่าเดิม0_78เพี้ยน()
    {
        static DocumentLineRequest L(decimal q, decimal up, decimal disc)
            => new(Description: "แผ่นรองซับ", Quantity: q, Unit: "ชิ้น", UnitPrice: up, DiscountPercent: 0m, VatRate: 7m,
                WithholdingTaxRate: 0m, AccountId: null, DiscountAmount: disc);
        var fixedT = DocumentService.PreviewTotals(new[] { L(1m, 1330m, 293m) }, pricesIncludeVat: true, 0m);
        Assert.Equal(969.16m, fixedT.Net);
        Assert.Equal(67.84m, fixedT.Vat);
        Assert.Equal(1037.00m, fixedT.Total);
        // ก่อนแก้: จำนวน 0.78 (ที่หน้าจอโชว์) ไม่มีส่วนลด ⇒ เปิดแก้แล้วบันทึก round(0.78 × 1,330) = 1,037.40 ≠ กระดาษ
        var oldT = DocumentService.PreviewTotals(new[] { L(0.78m, 1330m, 0m) }, pricesIncludeVat: true, 0m);
        Assert.Equal(1037.40m, oldT.Total);
    }

    [Fact]
    public void ส่วนลดเป็นเปอร์เซ็นต์บนแถว_รับเมื่อเท่าส่วนต่างทุกสตางค์_ไม่รับ7เปอร์เซ็นต์ที่ชนคอลัมน์VAT()
    {
        // 20% ไม่ใช่อัตราหัก ณ ที่จ่าย ⇒ % บนแถวคือส่วนลด (หมายเหตุแจ้ง) · 10% เท่าอัตราหัก ณ ที่จ่าย (ปันผล) ⇒ ต้องมีหัวคอลัมน์ส่วนลด (ทดสอบแยกด้านล่าง)
        var pct = OcrLineQuantityGuard.Decide(2m, 500m, 800m, null, Paper("ถังน้ำ 2 ใบ 500.00 20% 800.00"));
        Assert.Equal(OcrLineQuantityAction.PrintedDiscount, pct.Action);
        Assert.Equal(200.00m, pct.LineDiscount);
        Assert.False(pct.Blocking);

        // "7%" บนแถวคือคอลัมน์ VAT ได้ ⇒ ไม่ตีเป็นส่วนลดจากกระดาษ · จำนวน 1 พิมพ์บนแถว ⇒ คงจำนวน (ไม่หาร 0.93) · ส่วนต่าง 7 เก็บเป็นส่วนลดที่ระบุชัด
        // ให้บรรทัดลงตัว (ฝ่ายค้าน P2-5) + แท็กบล็อก "ส่วนต่างที่กระดาษไม่บอก"
        var vat = OcrLineQuantityGuard.Decide(1m, 100m, 93m, null, Paper("สินค้า 1 ชิ้น 100.00 7% 93.00"));
        Assert.Equal(OcrLineQuantityAction.KeepPrintedGap, vat.Action);
        Assert.Null(vat.Quantity);
        Assert.Equal(7m, vat.LineDiscount);
        Assert.True(vat.Blocking);
        Assert.Contains("ส่วนต่างที่กระดาษไม่บอก", vat.Note);
    }

    [Fact]
    public void ส่วนลดเล็กในเกณฑ์2เปอร์เซ็นต์_มีหัวคอลัมน์ส่วนลด_เป็นส่วนลด_ไม่มีหัว_แท็กบล็อก_เศษปัดราคาต่อหน่วยที่ไม่มีบนกระดาษ_ไม่แตะ()
    {
        var small = OcrLineQuantityGuard.Decide(1m, 1000m, 985m, null,
            Paper("รายการ จำนวน ราคา ส่วนลด จำนวนเงิน\nปั๊มน้ำ 1 เครื่อง 1,000.00 15.00 985.00"));
        Assert.Equal(OcrLineQuantityAction.PrintedDiscount, small.Action);
        Assert.Equal(15.00m, small.LineDiscount);

        // ฝ่ายค้าน P1-1 ข้อ c/d: ตัวเลขเปล่า 15.00 ไม่มีหัวคอลัมน์/ป้ายส่วนลด ⇒ ไม่ใช่ "ส่วนลดจากกระดาษ" · ในเกณฑ์ ±2% ก็ไม่เงียบ (เดิม Keep)
        var bare = OcrLineQuantityGuard.Decide(1m, 1000m, 985m, null, Paper("ปั๊มน้ำ 1 เครื่อง 1,000.00 15.00 985.00"));
        Assert.Equal(OcrLineQuantityAction.UnevidencedDiscount, bare.Action);
        Assert.Equal(15.00m, bare.LineDiscount);   // ยอดตรงกระดาษ บรรทัดลงตัว
        Assert.True(bare.Blocking);

        // Lazada 4 × 1,228.04 = 4,912.16 แต่พิมพ์ 4,912.15 — ไม่มี 0.01 บนแถว ⇒ ไม่แต่งส่วนลด (ตัวสร้างบรรทัดย้ายเศษไปผลต่างปัดเศษเหมือนเดิม)
        var lazada = OcrLineQuantityGuard.Decide(4m, 1228.04m, 4912.15m, null, Paper(OcrPaperSamples.ScommerceLazada));
        Assert.Equal(OcrLineQuantityAction.Keep, lazada.Action);
    }

    [Fact]
    public void ตัวเลขที่เท่าVATของบรรทัด_อยู่ระหว่างราคารวมVATกับยอดก่อนVAT_ไม่ตีเป็นส่วนลด()
    {
        // คอลัมน์ ราคารวม VAT · VAT · ยอดก่อน VAT: 107.00 − 7.00 = 100.00 ลงตัวก็จริง แต่ 7.00 คือ VAT ได้เท่ากัน ⇒ ไม่แต่งส่วนลด (ให้คนดู)
        var d = OcrLineQuantityGuard.Decide(1m, 107m, 100m, null, Paper("กาแฟ 1 ถุง 107.00 7.00 100.00"));
        Assert.Equal(OcrLineQuantityAction.KeepPrintedGap, d.Action);
        Assert.Equal(7m, d.LineDiscount);   // ส่วนต่างเก็บเป็นส่วนลดที่ระบุชัด (P2-5) — ไม่ใช่ "ส่วนลดจากกระดาษ"
        Assert.True(d.Blocking);
    }

    [Fact]
    public void ตัวเลขจำนวนที่อยู่ระหว่างราคากับยอด_ห้ามนับเป็นส่วนลด()
    {
        // คอลัมน์ ราคา · จำนวน · ยอด: 10.00 × 50 = 500 แต่ยอด 450 ⇒ ส่วนต่าง 50 เท่าเลขจำนวนพอดี — เลขนั้นคือจำนวน ไม่ใช่ส่วนลด
        var d = OcrLineQuantityGuard.Decide(50m, 10m, 450m, null, Paper("ท่อพีวีซี 10.00 50 450.00"));
        Assert.Equal(OcrLineQuantityAction.KeepPrintedGap, d.Action);   // ไม่ใช่ PrintedDiscount/UnevidencedDiscount ที่อ้างเลข 50
        Assert.Null(d.Quantity);
        Assert.True(d.Blocking);
    }

    // ── ทิศตรงข้าม: จำนวนที่ engine อ่านไม่ได้พิมพ์บนแถว ⇒ หารจำนวนจากยอดยังถูก (แต่ติดแท็ก) ────────────────────────────────

    [Fact]
    public void ทิศตรงข้าม_สินค้าชั่งน้ำหนัก_กระดาษไม่พิมพ์จำนวน_จำนวนเท่ากับยอดหารราคา0_45()
    {
        var d = OcrLineQuantityGuard.Decide(1m, 120m, 54m, null, Paper("หมูสับ (ราคาต่อ กก.) 120.00 54.00"));
        Assert.Equal(OcrLineQuantityAction.FixQuantity, d.Action);
        Assert.Equal(0.450m, d.Quantity);
        Assert.StartsWith(OcrLineQuantityGuard.Tag, d.Note);   // 0.45 ไม่ได้พิมพ์บนกระดาษ = ยังไม่พิสูจน์ ⇒ บล็อก
        Assert.True(d.Blocking);
    }

    [Fact]
    public void ทิศตรงข้าม_engineอ่านจำนวน1_กระดาษพิมพ์2_จำนวนจากยอดหารราคาตรงเลขบนกระดาษ_พิสูจน์แล้วแจ้งไม่บล็อก()
    {
        var d = OcrLineQuantityGuard.Decide(1m, 10m, 20m, null, Paper("ปากกาเจล 2 ด้าม 10.00 20.00"));
        Assert.Equal(OcrLineQuantityAction.FixQuantity, d.Action);
        Assert.Equal(2m, d.Quantity);
        // ฝ่ายค้าน P2-3: จำนวนใหม่พิมพ์ติดก่อนราคาบนกระดาษ = พิสูจน์แล้ว ⇒ แจ้ง ไม่บล็อก
        Assert.StartsWith(OcrLineQuantityGuard.NoteTag, d.Note);
        Assert.False(d.Blocking);
    }

    [Fact]
    public void ทิศตรงข้าม_เคสข_จำนวนคือยอดที่อ่านหลงคอลัมน์_ยังแก้จำนวนแม้ตัวเลขนั้นอยู่บนแถว()
    {
        // engine เอายอด 1,180 มาใส่จำนวน · บนแถวมี 1,180.00 (ยอด) ⇒ จำนวน = 1,180 ÷ 10 = 118
        var d = OcrLineQuantityGuard.Decide(1180m, 10m, 1180m, null, Paper("ถุงมือ 118 คู่ 10.00 1,180.00"));
        Assert.Equal(OcrLineQuantityAction.FixQuantity, d.Action);
        Assert.Equal(118m, d.Quantity);
        Assert.False(d.Blocking);   // 118 พิมพ์คู่หน่วย "คู่" ก่อนราคา = พิสูจน์แล้ว
    }

    [Fact]
    public void ทิศตรงข้าม_เคสก_ราคาเท่ายอดทั้งบรรทัด_จำนวนคือมิเตอร์_แก้ราคาไม่แก้จำนวน_มิเตอร์พิมพ์คู่หน่วยแจ้งไม่บล็อก()
    {
        var d = OcrLineQuantityGuard.Decide(3611m, 16351.48m, 16351.48m, null, Paper("ค่าไฟฟ้า 3,611 หน่วย 16,351.48"));
        Assert.Equal(OcrLineQuantityAction.FixUnitPrice, d.Action);
        Assert.Null(d.Quantity);
        Assert.Equal(4.5282m, d.UnitPrice);
        // ฝ่ายค้าน P2-3: บิลค่าไฟทุกเดือน — มิเตอร์ 3,611 พิมพ์คู่ "หน่วย" แล้วยอดตามมา = พิสูจน์แล้ว ⇒ แจ้ง (เดิมบล็อกทุกเดือน)
        Assert.StartsWith(OcrLineQuantityGuard.NoteTag, d.Note);
        Assert.False(d.Blocking);
        // ทิศตรงข้าม: ไม่มีข้อความกระดาษ / มิเตอร์ไม่ได้พิมพ์คู่หน่วย ⇒ ยังบล็อก
        Assert.True(OcrLineQuantityGuard.Decide(3611m, 16351.48m, 16351.48m, null, null).Blocking);
        Assert.True(OcrLineQuantityGuard.Decide(3611m, 16351.48m, 16351.48m, null, Paper("ค่าไฟฟ้า 16,351.48 รวม 16,351.48")).Blocking);
        // ราคาที่พิมพ์คั่นกลางขัดกับราคาที่ตั้ง ⇒ ไม่พิสูจน์
        Assert.True(OcrLineQuantityGuard.Decide(3611m, 16351.48m, 16351.48m, null, Paper("ค่าไฟฟ้า 3,611 หน่วย 5.00 16,351.48")).Blocking);
    }

    [Fact]
    public void จำนวนพิมพ์บนแถว_ส่วนต่างไม่มีส่วนลดอธิบาย_ห้ามแต่งจำนวน_คงค่าและติดแท็ก()
    {
        var d = OcrLineQuantityGuard.Decide(2m, 100m, 150m, null, Paper("สายยาง 2 ม้วน 100.00 150.00"));
        Assert.Equal(OcrLineQuantityAction.KeepPrintedGap, d.Action);
        Assert.Null(d.Quantity);
        Assert.Null(d.UnitPrice);
        // ฝ่ายค้าน P2-5: ห้ามเก็บ 2 × 100 − 0 ≠ 150 — ส่วนต่าง 50 เป็นส่วนลดที่ระบุชัด (ยอดตรงกระดาษ · บรรทัดลงตัว) + แท็กบล็อก
        Assert.Equal(50m, d.LineDiscount);
        Assert.Contains("ส่วนต่างที่กระดาษไม่บอก — ตรวจ", d.Note);
        Assert.StartsWith(OcrLineQuantityGuard.Tag, d.Note);
        Assert.False(OcrPostingReadiness.Evaluate(d.Note, hasUsableDate: true).CanAutoApprove);
    }

    [Fact]
    public void ลงตัวอยู่แล้ว_หรือข้อมูลไม่พอ_ไม่แตะ_และราคาว่างเติมจากยอดหารจำนวนแบบเดิม()
    {
        Assert.Equal(OcrLineQuantityAction.Keep, OcrLineQuantityGuard.Decide(2m, 268m, 536m, null, Paper(OcrPaperSamples.UptoyouShopee)).Action);
        Assert.Equal(OcrLineQuantityAction.Keep, OcrLineQuantityGuard.Decide(null, 100m, 50m, null, null).Action);
        Assert.Equal(OcrLineQuantityAction.Keep, OcrLineQuantityGuard.Decide(1m, 100m, 0m, null, null).Action);
        var fill = OcrLineQuantityGuard.Decide(3611m, null, 16351.48m, null, null);
        Assert.Equal(OcrLineQuantityAction.FillUnitPrice, fill.Action);
        Assert.Equal(4.5282m, fill.UnitPrice);
        Assert.Null(fill.Note);
        // ส่วนลดที่เอกสารประกาศแล้ว (e-Tax) อธิบายยอดได้ ⇒ ไม่แตะ ไม่อ่านกระดาษซ้ำ
        Assert.Equal(OcrLineQuantityAction.Keep, OcrLineQuantityGuard.Decide(3m, 37m, 84.32m, 26.68m, null).Action);
    }

    [Fact]
    public void ตัวแยกตัวเลข_ช่องติดกันของPdfPig_แตกถูกตัว()
    {
        var nums = Paper("แผน1.00ชิ้น1,330.00293.007%1,037.00(ต").Numbers;
        Assert.Equal(new[] { 1.00m, 1330.00m, 293.00m, 7m, 1037.00m }, nums.Select(n => n.Value).ToArray());
        Assert.Equal(new[] { false, false, false, true, false }, nums.Select(n => n.IsPercent).ToArray());
    }

    [Fact]
    public void ธงรวมVAT_เฉพาะใบที่มีส่วนลดจากกระดาษ_และหัวใบพิสูจน์เคสA()
    {
        Assert.True(OcrLineQuantityGuard.PaperDiscountPricesIncludeVat(true, 1037m, 969.16m, 67.84m, 1037m, null));
        Assert.False(OcrLineQuantityGuard.PaperDiscountPricesIncludeVat(false, 1037m, 969.16m, 67.84m, 1037m, null));   // ไม่มีส่วนลดจากกระดาษ = ไม่แตะ
        Assert.False(OcrLineQuantityGuard.PaperDiscountPricesIncludeVat(true, 969.16m, 969.16m, 67.84m, 1037m, null));  // ราคาก่อน VAT (เคส B)
        Assert.False(OcrLineQuantityGuard.PaperDiscountPricesIncludeVat(true, 1037m, null, null, 1037m, null));         // ไม่มี VAT
    }
}
