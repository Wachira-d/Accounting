using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 195 ฝ่ายค้าน C1 — <see cref="OcrHeaderVatEvidence"/>: "VAT หัวใบอ่านมาจากกระดาษ หรือระบบคำนวณเอง"
///
/// <para><b>ครึ่งที่ 1</b> (ใบที่ต้องถูกจับ): ใบผักรวม 1,070 ไม่พิมพ์ VAT ⇒ VAT 70.00 ที่ระบบแยก 7/107 เอง = <see cref="OcrHeaderVatSource.NotOnPaper"/>
/// ⇒ [VAT-DERIVED] ⇒ <see cref="OcrPostingReadiness"/> ห้ามอนุมัติเอง · <b>ครึ่งที่ 2</b> (ใบที่ถูกอยู่แล้วต้องไม่ถูกแตะ): กระดาษจริงทุกใบใน
/// <see cref="OcrPaperSamples"/> ที่พิมพ์ VAT = <see cref="OcrHeaderVatSource.Labelled"/> (ไม่มีคำเตือนเพิ่ม · ชั้นพิสูจน์ทั้งใบยังทำงาน)</para>
/// </summary>
public class OcrHeaderVatEvidenceTests
{
    // ── ครึ่งที่ 2: กระดาษจริงที่พิมพ์ VAT ไว้ — ต้องเป็น Labelled ทุกใบ (ห้ามเตือนใบถูก) ──────────────────────────
    [Theory]
    [InlineData(nameof(OcrPaperSamples.WinePro), 235.06)]                   // ตารางสรุป VAT% (ไม่มีป้ายบนแถวตัวเลข)
    [InlineData(nameof(OcrPaperSamples.HardwareBillDiscount), 92.77)]
    [InlineData(nameof(OcrPaperSamples.SupermarketMemberDiscount), 48.10)]
    [InlineData(nameof(OcrPaperSamples.WholesaleMixedVat), 28.00)]
    [InlineData(nameof(OcrPaperSamples.MakroPage3of3), 1148.28)]            // ตารางรหัส ภ.พ.
    [InlineData(nameof(OcrPaperSamples.UptoyouShopee), 35.07)]
    [InlineData(nameof(OcrPaperSamples.ScommerceLazada), 328.67)]
    public void กระดาษจริงที่พิมพ์VAT_เป็นLabelled_ไม่ติดVATDERIVED(string sample, double vat)
    {
        var raw = (string)typeof(OcrPaperSamples).GetField(sample)!.GetValue(null)!;
        var src = OcrHeaderVatEvidence.Classify(raw, null, (decimal)vat, null);
        Assert.Equal(OcrHeaderVatSource.Labelled, src);
        Assert.Null(OcrHeaderVatEvidence.DerivedNote(src, (decimal)vat));
    }

    [Fact]
    public void Azure_ป้ายVATกับตัวเลขคนละบรรทัด_ข้ามบรรทัดเปอร์เซ็นต์ได้_ยังนับเป็นLabelled()
    {
        Assert.True(OcrPaperAmounts.IsVatLabelled("ภาษีมูลค่าเพิ่ม 7% / VAT 7%\n328.67\nรวมเงินทั้งสิ้น\n5,024.00", 328.67m));
        Assert.True(OcrPaperAmounts.IsVatLabelled("VAT\n7%\n\n328.67", 328.67m));
        // ป้าย "VAT RATE" แล้วตัวเลข 7.00 บรรทัดถัดไป = อัตรา ไม่ใช่ยอดภาษี
        Assert.False(OcrPaperAmounts.IsVatLabelled("VAT RATE\n7.00", 7.00m));
        // ป้ายรวม VAT (ยอดสินค้า) แล้วตัวเลขบรรทัดถัดไป ไม่ใช่ยอดภาษี
        Assert.False(OcrPaperAmounts.IsVatLabelled("ราคารวมภาษีมูลค่าเพิ่ม\n70.00", 70.00m));
        // ตัวเลขไม่ตรง
        Assert.False(OcrPaperAmounts.IsVatLabelled("ภาษีมูลค่าเพิ่ม 7%\n328.67", 70.00m));
    }

    [Fact]
    public void eTaxXmlที่ลงนาม_นับเป็นLabelled()
        => Assert.Equal(OcrHeaderVatSource.Labelled,
            OcrHeaderVatEvidence.Classify("<rsm:TaxInvoice>…</rsm:TaxInvoice>", null, 328.67m, OcrHeaderVatEvidence.SignedXmlEngine));

    [Fact]
    public void ไม่มีVAT_ไม่มีอะไรให้ตัดสิน()
    {
        Assert.Equal(OcrHeaderVatSource.NoVat, OcrHeaderVatEvidence.Classify("ใบเสร็จ\nรวม 1,070.00", null, 0m, null));
        Assert.Null(OcrHeaderVatEvidence.DerivedNote(OcrHeaderVatSource.NoVat, 0m));
    }

    // ── ครึ่งที่ 1: VAT ที่ไม่มีบนกระดาษ ─────────────────────────────────────────────────────────────────

    private const string Vegetable =
        "ร้านผักสดป้าแดง\nเลขประจำตัวผู้เสียภาษี 0105556012341\nใบเสร็จรับเงิน/ใบกำกับภาษี\n"
        + "ผักกาดขาว 500.00\nผลไม้รวม 570.00\nรวมทั้งสิ้น 1,070.00\nสินค้าทุกรายการได้รับการยกเว้นภาษีมูลค่าเพิ่ม";

    [Fact]
    public void ใบผักรวม1070_VAT70ที่ระบบแยกเอง_NotOnPaper_และหยุดอนุมัติเอง()
    {
        var src = OcrHeaderVatEvidence.Classify(Vegetable, null, 70.00m, null);
        Assert.Equal(OcrHeaderVatSource.NotOnPaper, src);
        var note = OcrHeaderVatEvidence.DerivedNote(src, 70.00m);
        Assert.NotNull(note);
        Assert.Contains("70.00", note);
        var notes = "[Tier] Tesseract\n" + OcrHeaderVatEvidence.DerivedTag + " " + note;
        var v = OcrPostingReadiness.Evaluate(notes, hasUsableDate: true);
        Assert.False(v.CanAutoApprove);
        Assert.Contains("ไม่ได้พิมพ์", v.Reason);
        // แท็กติดไปกับสำเนาเมื่ออัปไฟล์ซ้ำ (ทุก blocking tag อยู่ใน DecisionNoteTags)
        Assert.Contains(OcrHeaderVatEvidence.DerivedTag, OcrScanSnapshot.DecisionNotes(notes));
    }

    // ── รอบ 195 ฝ่ายค้านรอบสอง R2-4: เลขที่บังเอิญตรง ≠ พิมพ์บนกระดาษ เมื่อมีร่องรอยว่าระบบถอดเอง ─────────────────────
    // เทสต์รุ่นก่อน ("เลขเท่าVATบังเอิญพิมพ์เป็นยอดบรรทัด_…_ไม่ใช่ค่าแต่ง") ล็อก PrintedUnlabelled ไว้โดยไม่รู้ที่มาของ VAT — แต่เส้นจริง
    // ของใบนี้ (มีเลขผู้ขาย + คำว่าใบกำกับภาษี · ไม่พิมพ์ VAT) VAT 70 คือค่าที่ระบบถอด 7/107 เอง ⇒ ต้องเป็น NotOnPaper + ติด [VAT-DERIVED]
    // เหลือ PrintedUnlabelled เฉพาะเมื่อไม่มีร่องรอยการถอด (VAT มาจาก engine อ่านตัวเลขผิดที่) หรือผู้ใช้แก้ VAT จนไม่ใช่ค่าที่ถอดแล้ว

    private const string ShipFee =
        "ร้าน ก\nเลขประจำตัวผู้เสียภาษี 0105556012341\nใบกำกับภาษี\nสินค้า 1,000.00\nค่าส่ง 70.00\nรวม 1,070.00";

    private const string BackCalcNotes =
        "[Tier] Tesseract\n[Reasoning]\n  • [VAT back-calc] กระดาษไม่ได้พิมพ์ยอด VAT ไว้ — ระบบคำนวณจากยอดรวม (7/107)";

    [Fact]
    public void ค่าส่ง70บังเอิญตรงVATที่ถอดเอง_มีร่องรอยถอด_NotOnPaper_และติดVATDERIVED()
    {
        Assert.Equal(OcrHeaderVatSource.NotOnPaper, OcrHeaderVatEvidence.Classify(ShipFee, null, 70.00m, null, BackCalcNotes));
        var src = OcrHeaderVatEvidence.Classify(ShipFee, null, 70.00m, null, BackCalcNotes, paperTotal: 1070.00m);
        Assert.Equal(OcrHeaderVatSource.NotOnPaper, src);
        Assert.NotNull(OcrHeaderVatEvidence.DerivedNote(src, 70.00m));
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่มีร่องรอยถอด_เลขบังเอิญตรง_ยังเป็นPrintedUnlabelled()
    {
        Assert.Equal(OcrHeaderVatSource.PrintedUnlabelled, OcrHeaderVatEvidence.Classify(ShipFee, null, 70.00m, null));
        Assert.Equal(OcrHeaderVatSource.PrintedUnlabelled,
            OcrHeaderVatEvidence.Classify(ShipFee, null, 70.00m, null, "[Tier] Azure DI\n[VAT skip] ไม่แยก"));
        Assert.Null(OcrHeaderVatEvidence.DerivedNote(OcrHeaderVatSource.PrintedUnlabelled, 70.00m));
    }

    [Fact]
    public void ทิศตรงข้าม_ร่องรอยถอดแต่VATไม่ใช่ค่าที่ถอดจากยอดนี้แล้ว_ไม่ถือร่องรอยเก่า()
        // ยอดรวม 2,000 ⇒ ค่าที่ถอดคือ 130.84 · VAT 70 ที่ถืออยู่ = ผู้ใช้/ขั้นอื่นแก้แล้ว ⇒ ตัดสินจากกระดาษตามเดิม
        => Assert.Equal(OcrHeaderVatSource.PrintedUnlabelled,
            OcrHeaderVatEvidence.Classify(ShipFee, null, 70.00m, null, BackCalcNotes, paperTotal: 2000.00m));

    [Fact]
    public void ทิศตรงข้าม_ร่องรอยถอดแต่VATพิมพ์คู่ป้ายบนกระดาษ_ยังLabelled()
        => Assert.Equal(OcrHeaderVatSource.Labelled,
            OcrHeaderVatEvidence.Classify(OcrPaperSamples.ScommerceLazada, null, 328.67m, null, BackCalcNotes, 5024.00m));

    // ── รอบ 195 ฝ่ายค้านรอบสอง (ค): ป้าย VAT รูปอื่นที่เคยไม่ถูกจำ ⇒ ตัวพิสูจน์ทั้งใบหยุดทำงานบนใบถูก ─────────────────

    [Theory]
    [InlineData("Subtotal 1,000.00\nValue Added Tax 7% 70.00\nGrand Total 1,070.00")]          // อังกฤษเต็มคำ แถวเดียว
    [InlineData("Value Added Tax 7%\n70.00\nGrand Total\n1,070.00")]                              // อังกฤษเต็มคำ คนละบรรทัด
    [InlineData("VALUE-ADDED TAX 70.00")]
    [InlineData("VAT | 70.00 | Total | 1,070.00")]                                                   // ป้าย/เลขคนละคอลัมน์ แถวเดียว
    [InlineData("รวมเงิน\nภาษีมูลค่าเพิ่ม 7%\nรวมทั้งสิ้น\n1,000.00\n70.00\n1,070.00")]           // คอลัมน์ป้าย → คอลัมน์ตัวเลข
    [InlineData("ภาษีมูลค่าเพิม 7% 70.00")]                                                          // Tesseract ไม้เอกหล่น (ข้อความดิบ)
    public void ป้ายVATรูปอื่น_นับเป็นLabelled(string raw)
        => Assert.Equal(OcrHeaderVatSource.Labelled, OcrHeaderVatEvidence.Classify(raw, null, 70.00m, null));

    [Theory]
    [InlineData("รวมเงิน\nภาษีมูลค่าเพิ่ม 7%\nส่วนลด\nรวมทั้งสิ้น\n1,000.00\n70.00\n1,070.00")]  // 4 ป้าย 3 ตัวเลข — จับคู่ไม่ได้แน่ ไม่เดา
    [InlineData("VAT | 1,000.00 | Total")]                                                         // ตัวเลขแรกหลังป้ายไม่ใช่ 70 (70 ไม่อยู่บนใบ)
    [InlineData("ค่าส่ง 70.00 | VAT | 1,000.00")]                                                   // 70 อยู่ก่อนป้าย ไม่ใช่รูปป้าย→ตัวเลข
    [InlineData("Value Added Tax Included 70.00")]                                                   // ยอดรวม VAT ไม่ใช่ยอดภาษี
    [InlineData("VAT Registration No. 70.00")]
    [InlineData("รวมเงิน\nรวมทั้งสิ้น\n1,000.00\n70.00")]                                           // ไม่มีป้าย VAT ในคอลัมน์เลย
    public void ทิศตรงข้าม_รูปที่ไม่ใช่ยอดVAT_ไม่นับเป็นLabelled(string raw)
        => Assert.NotEqual(OcrHeaderVatSource.Labelled, OcrHeaderVatEvidence.Classify(raw, null, 70.00m, null));

    [Fact]
    public void ตัวnormalizeซ่อมภาษีมูลค่าเพิม_และidempotent()
    {
        var once = Accounting.Services.Implementations.Ocr.ThaiTextNormalizer.Normalize("ภาษีมูลค่าเพิม 7% 92.77");
        Assert.Equal("ภาษีมูลค่าเพิ่ม 7% 92.77", once);
        Assert.Equal(once, Accounting.Services.Implementations.Ocr.ThaiTextNormalizer.Normalize(once));
        Assert.Equal("ภาษีมูลค่าเพิ่ม", Accounting.Services.Implementations.Ocr.ThaiTextNormalizer.Normalize("ภาษีมูลค่าเพิ่ม"));
    }

    // ── รอบ 195 ฝ่ายค้านรอบสอง (PLAUSIBLE ก): ข้อความต้องบอก ม.86/4(6) ⇒ ม.82/5(1) + ทางเลือก ─────────────────────────────

    [Fact]
    public void ข้อความVATไม่อยู่บนกระดาษ_อ้างม86_4_6และม82_5_1_พร้อมทางเลือก()
    {
        var note = OcrHeaderVatEvidence.DerivedNote(OcrHeaderVatSource.NotOnPaper, 70.00m)!;
        Assert.Contains("ม.86/4(6)", note);
        Assert.Contains("ม.82/5(1)", note);
        Assert.Contains("ตั้ง VAT เป็น 0 แล้วลงค่าใช้จ่ายเต็มจำนวน", note);
        Assert.Contains("ราคารวมภาษีมูลค่าเพิ่มแล้ว", note);
        Assert.Contains("แก้ยอดตามกระดาษ", note);   // ทางไปต่อเมื่อระบบอ่านผิด (ไม่ใช่ใบไม่ครบ)
    }

    [Fact]
    public void Tesseractทำวรรณยุกต์หล่น_ใช้ข้อความที่normalizeแล้วได้()
    {
        // "ภาษีมูลค่าเพื่" (Tesseract ทำ ิ↔ื + ม หล่น) — ป้ายบนข้อความดิบไม่ครบคำ · ตัว normalize ซ่อมเป็น "ภาษีมูลค่าเพิ่ม"
        const string raw = "ภาษีมูลค่าเพื่ 7%   92.77";
        var normalized = Accounting.Services.Implementations.Ocr.ThaiTextNormalizer.Normalize(raw);
        Assert.False(OcrPaperAmounts.IsVatLabelled(raw, 92.77m));
        Assert.Equal(OcrHeaderVatSource.Labelled, OcrHeaderVatEvidence.Classify(raw, normalized, 92.77m, null));
    }

    // ── ดึงรายการซ้ำ: ล้าง [Σ-GAP]/[VAT-DERIVED] ของรอบก่อน · ธงอื่นคงเดิม (ฝ่ายค้าน P3) ──────────────────────

    [Fact]
    public void ดึงรายการซ้ำ_ล้างGapเก่า_ธงอื่นคงเดิมทุกตัวอักษร()
    {
        const string notes = "[Tier] Azure DI สำเร็จ\n[VAT-CLAIM] ใบกำกับภาษีอย่างย่อ §82/5(2)\n"
            + "[Σ-GAP] ยอดรวมจากรายการ 4,695.33 + VAT 0.00 = 4,695.33 ≠ ยอดรวมบนกระดาษ 5,024.00\n"
            + "[Σ] กระทบยอดผ่าน\n[VAT-DERIVED] VAT 70.00 ไม่ได้พิมพ์\n[APPROVE-SKIP] ผลรวมรายการไม่ตรง\n"
            + "[Reasoning]\n  • [Σ-GAP] ข้อความในเหตุผล (ไม่ใช่บรรทัดแท็ก)";
        var stripped = OcrLineBuildNotes.StripRecomputed(notes)!;
        Assert.DoesNotContain("\n[Σ-GAP]", stripped);
        Assert.DoesNotContain("[VAT-DERIVED]", stripped);
        Assert.Contains("[VAT-CLAIM] ใบกำกับภาษีอย่างย่อ §82/5(2)", stripped);
        Assert.Contains("[Σ] กระทบยอดผ่าน", stripped);
        Assert.Contains("[APPROVE-SKIP] ผลรวมรายการไม่ตรง", stripped);
        Assert.Contains("  • [Σ-GAP] ข้อความในเหตุผล", stripped);      // บรรทัดเหตุผลไม่ใช่แท็กของตัวสร้าง — คงไว้
        Assert.Equal(6, stripped.Split('\n').Length);
    }

    [Fact]
    public void ดึงรายการซ้ำ_ไม่มีอะไรให้ล้าง_คืนค่าเดิม_และบรรทัดใหม่ที่ยังไม่ตรงกลับมาได้()
    {
        const string clean = "[Tier] Azure DI สำเร็จ\n[Σ] กระทบยอดผ่าน";
        Assert.Same(clean, OcrLineBuildNotes.StripRecomputed(clean));
        Assert.Null(OcrLineBuildNotes.StripRecomputed(null));
        // ทิศตรงข้าม: ตัวสร้างเขียน [Σ-GAP] ใหม่หลังล้าง ⇒ readiness ยังหยุด (ล้าง ≠ ปิดด่าน)
        var rebuilt = OcrLineBuildNotes.StripRecomputed("[Σ-GAP] เก่า") + "\n" + OcrLineBuildNotes.GapTag + " ใหม่: ยังไม่ตรง";
        Assert.False(OcrPostingReadiness.Evaluate(rebuilt, hasUsableDate: true).CanAutoApprove);
        Assert.True(OcrPostingReadiness.Evaluate(OcrLineBuildNotes.StripRecomputed("[Σ-GAP] เก่า"), hasUsableDate: true).CanAutoApprove);
    }
}
