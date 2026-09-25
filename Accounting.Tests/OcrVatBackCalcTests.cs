using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 195 ฝ่ายค้าน C1 (ต้นเหตุร่วม) — ตัวแยก VAT จากยอดรวมทุกชุดต้องถามด่านเดียวกัน (<see cref="VatBackCalcGuard"/>)
///
/// <para>ก่อนแก้ back-calc มีสามชุด: <c>ParseThaiDocument</c> (ผ่านด่าน) · <c>AmountTripleExtractor</c> fallback (7/107 เมื่อมีคำ VAT
/// ที่ไหนก็ได้ แล้วถูกรับเป็น "สามค่าที่ลงตัว" ความมั่นใจ 0.95) · <c>SmartFieldExtractor.ApplyAmountMath</c> (ไม่ผ่านด่าน) — สองชุดหลัง
/// นับคำ "ยกเว้นภาษีมูลค่าเพิ่ม" เป็นหลักฐานว่ามี VAT และเติมทับหลังด่านปฏิเสธ ⇒ ใบผัก 1,070 ได้ VAT แต่ง 70.00</para>
///
/// <para><b>ครึ่งที่ 1</b> ใบผัก/ใบที่ด่านปฏิเสธ ⇒ VAT ว่าง · <b>ครึ่งที่ 2</b> ใบที่ด่านยอม (ผู้ขายจด VAT · รายการไม่ยกเว้น) ⇒ ยังแยกได้
/// พร้อมแท็ก [VAT back-calc] และความมั่นใจต่ำตามด่าน (ไม่ถูกดันเป็น 0.95)</para>
/// </summary>
public class OcrVatBackCalcTests
{
    private const string Seller = "0105556012341";

    private const string Vegetable =
        "ร้านผักสดป้าแดง\nเลขประจำตัวผู้เสียภาษี 0105556012341\nใบเสร็จรับเงิน\n"
        + "ผักกาดขาว 500.00\nผลไม้รวม 570.00\nรวมทั้งสิ้น 1,070.00\nสินค้าทุกรายการได้รับการยกเว้นภาษีมูลค่าเพิ่ม";

    // ── คำที่บอกว่า "ไม่มี VAT" ไม่ใช่หลักฐานว่ามี VAT ─────────────────────────────────────────────────
    [Theory]
    [InlineData("สินค้าทุกรายการได้รับการยกเว้นภาษีมูลค่าเพิ่ม")]
    [InlineData("ร้านนี้ไม่ได้จดทะเบียนภาษีมูลค่าเพิ่ม")]
    [InlineData("NON VAT")]
    [InlineData("Non-VAT item")]
    [InlineData("VAT exempt goods")]
    [InlineData("Total Value of Exempt VAT Amount")]
    public void วลีปฏิเสธVAT_ไม่นับว่าพูดถึงVAT(string text)
        => Assert.False(OcrVatBackCalc.MentionsVat(text));

    [Theory]
    [InlineData("ใบกำกับภาษี")]
    [InlineData("ราคาไม่รวมภาษีมูลค่าเพิ่ม 500.93")]           // ราคาก่อน VAT = มี VAT
    [InlineData("ภาษีมูลค่าเพิ่ม 7% 92.77")]
    [InlineData("TAX INVOICE")]
    [InlineData("ยกเว้นภาษีมูลค่าเพิ่ม 0.00\nVAT 7% 328.67")]    // ใบ Scommerce: มีแถวยกเว้น 0 และแถว VAT จริง
    public void คำที่บอกว่ามีVAT_ยังนับเหมือนเดิม(string text)
        => Assert.True(OcrVatBackCalc.MentionsVat(text));

    // ── ตัวตัดสิน ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void ใบผัก_คำยกเว้นอย่างเดียว_ไม่แยกVAT()
    {
        var p = OcrVatBackCalc.Plan(Vegetable, 1070m, Seller, new[] { "ผักกาดขาว", "ผลไม้รวม" }, null);
        Assert.Equal(OcrVatBackCalcAction.NotApplicable, p.Action);
        Assert.Null(p.VatAmount);
    }

    [Fact]
    public void ด่านเคยปฏิเสธแล้ว_ตัวแยกชุดที่สองไม่เติมทับ()
    {
        var trace = new[] { VatBackCalcGuard.SkipTag + " ทุกรายการบนใบเข้าข่ายสินค้ายกเว้น VAT (§81)" };
        var p = OcrVatBackCalc.Plan("ใบกำกับภาษี\nค่าบริการ 1,070.00\nรวม 1,070.00", 1070m, Seller, new[] { "ค่าบริการ" }, trace);
        Assert.Equal(OcrVatBackCalcAction.Skip, p.Action);
        Assert.Null(p.VatAmount);
        Assert.StartsWith(VatBackCalcGuard.SkipTag, p.Trace);
    }

    [Fact]
    public void รายการยกเว้นทั้งใบ_ด่านปฏิเสธ()
    {
        var p = OcrVatBackCalc.Plan("ใบกำกับภาษี\nผักกาดขาว 500.00\nผลไม้รวม 570.00\nรวม 1,070.00", 1070m, Seller,
            new[] { "ผักกาดขาว", "ผลไม้รวม" }, null);
        Assert.Equal(OcrVatBackCalcAction.Skip, p.Action);
        Assert.Contains("§81", p.Trace);
    }

    [Fact]
    public void ผู้ขายไม่มีเลข13หลักที่ถูกต้อง_ด่านปฏิเสธ()
        => Assert.Equal(OcrVatBackCalcAction.Skip,
            OcrVatBackCalc.Plan("ใบกำกับภาษี\nรวม 1,070.00", 1070m, null, new[] { "ค่าบริการ" }, null).Action);

    [Fact]
    public void ทิศตรงข้าม_ผู้ขายจดVAT_รายการไม่ยกเว้น_ยังแยกได้พร้อมแท็ก()
    {
        var p = OcrVatBackCalc.Plan("ใบกำกับภาษี\nค่าบริการล้างแอร์ 1,070.00\nรวม 1,070.00\nราคารวมภาษีมูลค่าเพิ่มแล้ว",
            1070m, Seller, new[] { "ค่าบริการล้างแอร์" }, null);
        Assert.Equal(OcrVatBackCalcAction.Apply, p.Action);
        Assert.Equal(1000.00m, p.SubTotal);
        Assert.Equal(70.00m, p.VatAmount);
        Assert.Equal(0.75d, p.Confidence);                    // กระดาษบอกว่าราคารวมภาษี (ด่าน) — ไม่ใช่ 0.95
        Assert.True(OcrVatBackCalc.WasBackCalculated(new[] { p.Trace! }));
    }

    // ── ทั้งเส้น Enrich (SmartFieldExtractor) — ค่าที่ออกมาจริง ────────────────────────────────────────
    [Fact]
    public void Enrich_ใบผักรวม1070_ไม่มีVAT70ปลอม()
    {
        var data = new Accounting.Services.Implementations.OcrExtractedData { TotalAmount = 1070m };
        SmartFieldExtractor.Enrich(data, Vegetable);
        Assert.Null(data.VatAmount);
        Assert.Equal(1070m, data.TotalAmount);
    }

    [Fact]
    public void Enrich_ด่านของParseThaiDocumentปฏิเสธแล้ว_Enrichไม่เติมทับ()
    {
        // เส้น Hybrid: ParseThaiDocument → [VAT skip] → Enrich (เดิมเติม 7/107 ทับ)
        var data = new Accounting.Services.Implementations.OcrExtractedData { TotalAmount = 1070m };
        data.ReasoningTrace.Add(VatBackCalcGuard.SkipTag + " ผู้ขายไม่มีเลขประจำตัวผู้เสียภาษี 13 หลักที่ถูกต้อง");
        SmartFieldExtractor.Enrich(data, "ใบกำกับภาษี\nเลขประจำตัวผู้เสียภาษี 0105556012341\nค่าบริการ 1,070.00\nรวมทั้งสิ้น 1,070.00");
        Assert.Null(data.VatAmount);
        Assert.Null(data.SubTotal);
    }

    [Fact]
    public void Enrich_ทิศตรงข้าม_ใบบริการราคารวมVAT_ยังแยกได้_ความมั่นใจไม่ถูกดันเป็น095()
    {
        var data = new Accounting.Services.Implementations.OcrExtractedData { TotalAmount = 1070m };
        data.Items.Add(new Accounting.Services.Implementations.OcrExtractedLineItem { Description = "ค่าบริการล้างแอร์", Amount = 1070m });
        SmartFieldExtractor.Enrich(data,
            "ใบกำกับภาษี\nเลขประจำตัวผู้เสียภาษี 0105556012341\nค่าบริการล้างแอร์ 1,070.00\nรวมทั้งสิ้น 1,070.00\nราคารวมภาษีมูลค่าเพิ่มแล้ว");
        Assert.Equal(70.00m, data.VatAmount);
        Assert.Equal(1000.00m, data.SubTotal);
        Assert.True(data.FieldConfidence["VatAmount"] < 0.85);   // ไฮไลต์เหลือง (กฎเหล็ก #3 ข้อ 3) — ค่าที่คำนวณ ไม่ใช่ค่าที่อ่าน
        Assert.Contains(data.ReasoningTrace, t => t.StartsWith(VatBackCalcGuard.BackCalcTag));
    }

    // ── AmountTripleExtractor: ไม่แต่ง VAT ใน fallback อีก (ที่เดียวที่ถอด VAT คือ ApplyAmountMath → ด่าน) ─────────
    [Fact]
    public void AmountTriple_ไม่มีสามค่าที่ลงตัว_คืนยอดรวมอย่างเดียว()
    {
        var (sub, vat, total) = AmountTripleExtractor.Extract(Vegetable);
        Assert.Null(sub);
        Assert.Null(vat);
        Assert.Equal(1070.00m, total);
    }

    [Fact]
    public void AmountTriple_ทิศตรงข้าม_สามค่าที่พิมพ์จริงยังหาเจอ()
    {
        var (sub, vat, total) = AmountTripleExtractor.Extract(OcrPaperSamples.HardwareBillDiscount);
        Assert.Equal(1325.25m, sub);
        Assert.Equal(92.77m, vat);
        Assert.Equal(1418.02m, total);
    }
}
