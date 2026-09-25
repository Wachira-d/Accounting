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

    [Fact]
    public void เลขเท่าVATบังเอิญพิมพ์เป็นยอดบรรทัด_ไม่ใช่หลักฐานพิสูจน์_แต่ก็ไม่ใช่ค่าแต่ง()
    {
        // "ค่าส่ง 70.00" บนใบ 1,000 + 70 = 1,070 ไม่มี VAT — ตัวพิสูจน์ต้องไม่เชื่อ · ตัวหยุดไม่เตือน (ไม่แต่ง)
        const string raw = "ร้าน ก\nสินค้า 1,000.00\nค่าส่ง 70.00\nรวม 1,070.00";
        Assert.Equal(OcrHeaderVatSource.PrintedUnlabelled, OcrHeaderVatEvidence.Classify(raw, null, 70.00m, null));
        Assert.Null(OcrHeaderVatEvidence.DerivedNote(OcrHeaderVatSource.PrintedUnlabelled, 70.00m));
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
