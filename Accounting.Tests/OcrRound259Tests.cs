using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 259 ชุด D (OCR ส่วนที่เหลือ) — ตัวตัดสิน pure ทั้งสองทิศ (กฎ #4 H: "ใบที่พังกลับมาถูก" และ "ใบที่ถูกอยู่แล้วไม่ถูกแตะ"):
/// C-M5 <see cref="OcrTextTableFallback"/> · <see cref="OcrBlankLine"/> · C-M2 <see cref="OcrHeaderAmounts.RederiveAfterTotalEdit"/> ·
/// C-L3 <see cref="OcrCounterpartyMatch.MissingBeforeCreateNote"/> · C-L4 <see cref="OcrDuplicateScanRule.DecideByContent"/>
/// </summary>
public class OcrRound259Tests
{
    // ───── C-M5: engine คืนตารางไม่ครบ ⇒ ตารางจากข้อความ (เฉพาะเมื่อลงยอดที่พิสูจน์แล้ว) ─────

    /// <summary>ข้อความตารางสองแถวที่ตัวแตกบรรทัดอ่านได้ครบ (ตัวเดียวกับ PdfTextOcr13Tests: Σ 490 ตรงยอด)</summary>
    private const string TwoRowTable = "รายการ\tจำนวน\tราคา\tส่วนลด\tจำนวนเงิน\nสินค้า A\t2\t100.00\t10.00\t190.00\nสินค้า B\t1\t300.00\t300.00\nรวมทั้งสิ้น\t490.00";

    [Fact]
    public void CM5_engineคืนแถวเดียวจากสอง_ยอดหัวใบพิสูจน์แล้ว_ใช้ตารางข้อความที่ลงยอด()
    {
        var r = OcrTextTableFallback.Decide(new[] { 190.00m }, null, 490.00m, TwoRowTable);
        Assert.True(r.Replace);
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(490.00m, r.Lines.Sum(l => l.Amount));
        Assert.StartsWith(OcrTextTableFallback.Tag, r.Note);
        Assert.Contains("ตรวจก่อนยืนยัน", r.Note);
    }

    [Fact]
    public void CM5_ทิศตรงข้าม_ตารางengineลงยอดอยู่แล้ว_ไม่แตะ()
        => Assert.False(OcrTextTableFallback.Decide(new[] { 190.00m, 300.00m }, null, 490.00m, TwoRowTable).Replace);

    [Fact]
    public void CM5_ทิศตรงข้าม_หัวใบยังไม่พิสูจน์_ไม่ใช้หัวใบชี้ขาดบรรทัด()
        => Assert.False(OcrTextTableFallback.Decide(new[] { 190.00m }, null, null, TwoRowTable).Replace);

    [Fact]
    public void CM5_ทิศตรงข้าม_ตารางข้อความไม่ลงยอดที่พิสูจน์_คงตารางengine()
        // ยอดที่พิสูจน์ 600 — ตารางข้อความได้ 490 (ไม่ลง) ⇒ ไม่แทน ([Σ-GAP] เดิมฟ้อง ผู้ใช้เห็นช่องว่าง)
        => Assert.False(OcrTextTableFallback.Decide(new[] { 190.00m }, null, 600.00m, TwoRowTable).Replace);

    [Fact]
    public void CM5_ทิศตรงข้าม_ตารางengineเกินยอด_ไม่ใช่ตารางขาด_ไม่แตะ()
        // Σ เกิน = แถวซ้ำ/สำเนา — งานของ OcrDuplicateLineGuard ไม่ใช่ตัวนี้
        => Assert.False(OcrTextTableFallback.Decide(new[] { 190.00m, 300.00m, 300.00m }, null, 490.00m, TwoRowTable).Replace);

    [Fact]
    public void CM5_ทิศตรงข้าม_engineไม่คืนแถวเลย_ไม่ใช่เส้นนี้()
        // 0 แถว = เส้นแตกบรรทัดเดิม (AI → นักเรียน → ข้อความ) ใน TrySplitLineItemsAsync
        => Assert.False(OcrTextTableFallback.Decide(Array.Empty<decimal>(), null, 490.00m, TwoRowTable).Replace);

    /// <summary>Golden ชุดกระดาษจริง (OcrReplayHarness.Corpus · กฎ #4 H): ใบที่ engine ให้แถวลงยอดอยู่แล้ว ต้องไม่ถูกแตะ ·
    /// และทุกใบที่ตัวนี้แทน ต้องได้ Σ ที่ลงยอดบนกระดาษ (ไม่มีทางได้ตารางที่ไม่ลงยอด)</summary>
    [Fact]
    public void CM5_golden_ชุดกระดาษจริง_ใบที่ลงยอดอยู่แล้วไม่ถูกแตะ_ใบที่ถูกแทนลงยอดเสมอ()
    {
        var touched = new List<string>();
        foreach (var p in OcrReplayHarness.Corpus)
        {
            var amounts = (p.EngineLines ?? p.Lines)?.Select(l => l.Amount).ToArray() ?? p.LineAmounts;
            if (amounts is not { Length: > 0 } || p.EngineTotal is not > 0m) continue;
            var r = OcrTextTableFallback.Decide(amounts, p.EngineSubTotal, p.EngineTotal, p.RawText);
            var sum = amounts.Sum();
            var reconciles = Math.Abs(sum - p.EngineTotal.Value) <= OcrLineSplitGuard.ToleranceBaht
                             || (p.EngineSubTotal is > 0m && Math.Abs(sum - p.EngineSubTotal.Value) <= OcrLineSplitGuard.ToleranceBaht);
            if (reconciles) Assert.False(r.Replace, $"{p.Name}: ตารางลงยอดอยู่แล้วแต่ถูกแทน");
            if (!r.Replace) continue;
            touched.Add(p.Name);
            var got = r.Lines.Sum(l => l.Amount);
            Assert.True(Math.Abs(got - p.EngineTotal.Value) <= OcrLineSplitGuard.ToleranceBaht
                        || (p.EngineSubTotal is > 0m && Math.Abs(got - p.EngineSubTotal.Value) <= OcrLineSplitGuard.ToleranceBaht),
                $"{p.Name}: ตารางที่แทนไม่ลงยอด ({got})");
            Assert.True(r.Lines.Count > amounts.Length, $"{p.Name}: แทนด้วยแถวไม่มากกว่าเดิม");
        }
        Assert.NotNull(touched);   // รายชื่อใบที่ถูกแทน (ถ้ามี) อยู่ในข้อความ assert ด้านบนเมื่อผิด
    }

    [Fact]
    public void CM5_เพิ่มบรรทัด_แถวว่างจริง_ต้องกรอกหรือลบก่อนสร้าง()
    {
        Assert.Equal(1, OcrBlankLine.Count(new (string?, decimal?, decimal?, decimal?)[] { ("", null, null, null) }));
        Assert.Equal(1, OcrBlankLine.Count(new (string?, decimal?, decimal?, decimal?)[] { ("  ", 0m, 0m, 1m) }));   // แถว 1 × 0.00 ที่ปุ่มเดิมสร้างไว้ (persist แล้ว) — ถูกจับด้วย
        Assert.Equal(2, OcrBlankLine.Count(new (string?, decimal?, decimal?, decimal?)[] { ("", null, null, null), ("ค่าขนส่ง", 50m, null, null), (null, 0m, null, null) }));
        Assert.Contains("ลบ", OcrBlankLine.Message(1));
    }

    [Fact]
    public void CM5_ทิศตรงข้าม_แถวที่มียอดแต่อ่านชื่อไม่ได้_ไม่ใช่แถวว่าง()
    {
        Assert.Equal(0, OcrBlankLine.Count(new (string?, decimal?, decimal?, decimal?)[] { ("", 120m, null, null), ("ค่าบริการ", null, null, null), ("", null, 100m, 2m) }));
        Assert.Equal(0, OcrBlankLine.Count(new (string?, decimal?, decimal?, decimal?)[] { ("ค่าบริการ", 100m, null, null) }));
    }

    // ───── C-M2: แก้เฉพาะยอดรวม ⇒ ถอดฐาน/VAT ใหม่ ─────

    [Fact]
    public void CM2_OCRอ่าน500_ผู้ใช้พิมพ์535_ได้ฐาน500_VAT35()
    {
        var (sub, vat, note) = OcrHeaderAmounts.RederiveAfterTotalEdit(467.29m, 32.71m, 500.00m, 535.00m);
        Assert.Equal(500.00m, sub);
        Assert.Equal(35.00m, vat);
        Assert.Contains("[TOTAL-EDIT]", note);
    }

    [Fact]
    public void CM2_ทิศตรงข้าม_ไม่ได้แก้ยอด_ฐานVATเดิมทุกสตางค์()
    {
        Assert.Equal(((decimal?)467.29m, (decimal?)32.71m, (string?)null), OcrHeaderAmounts.RederiveAfterTotalEdit(467.29m, 32.71m, 500.00m, 500.00m));
        Assert.Equal(((decimal?)467.29m, (decimal?)32.71m, (string?)null), OcrHeaderAmounts.RederiveAfterTotalEdit(467.29m, 32.71m, 500.00m, null));
    }

    [Fact]
    public void CM2_ใบไม่มีVAT_ฐานเท่ายอดใหม่_และใบVATไม่ใช่7เปอร์เซ็นต์ล้วน_คงVATไม่เดาสัดส่วน()
    {
        var (s0, v0, _) = OcrHeaderAmounts.RederiveAfterTotalEdit(500m, 0m, 500m, 535m);
        Assert.Equal(535m, s0);
        Assert.Equal(0m, v0);
        // ใบผสมยกเว้น (ฐาน 764 · VAT 28 ⇒ ไม่ใช่ 7% ของฐาน) — ห้ามแต่ง VAT ใหม่
        var (s1, v1, n1) = OcrHeaderAmounts.RederiveAfterTotalEdit(764m, 28m, 792m, 800m);
        Assert.Equal(28m, v1);
        Assert.Equal(772m, s1);
        Assert.Contains("ตรวจก่อนยืนยัน", n1);
    }

    // ───── C-L3: ป้ายต้องเลือกคู่ค้าก่อนสร้าง ─────

    [Fact]
    public void CL3_ฝั่งซื้อไม่มีชื่อผู้ขายและยังไม่จับคู่_เตือนก่อนสร้าง()
    {
        var note = OcrCounterpartyMatch.MissingBeforeCreateNote(false, null, "  ", null, null);
        Assert.Equal(OcrCounterpartyMatch.NoCounterpartyMessage(false), note);
        Assert.NotNull(OcrCounterpartyMatch.MissingBeforeCreateNote(true, null, "ร้านเรา", null, null));
    }

    [Fact]
    public void CL3_ทิศตรงข้าม_จับคู่แล้วหรือมีชื่อให้สร้างผู้ติดต่อ_ไม่เตือน()
    {
        Assert.Null(OcrCounterpartyMatch.MissingBeforeCreateNote(false, Guid.NewGuid(), null, null, null));
        Assert.Null(OcrCounterpartyMatch.MissingBeforeCreateNote(false, null, "ร้านวัสดุ", null, null));
        Assert.Null(OcrCounterpartyMatch.MissingBeforeCreateNote(true, null, null, null, "0105556000017"));
    }

    // ───── C-L4: ซ้ำด้วยเนื้อหาเมื่ออ่านเลขที่ไม่ได้ ─────

    private static readonly Guid Vendor = Guid.NewGuid();
    private static readonly DateTime Day = new(2026, 11, 3);

    private static OcrDuplicateScanRule.ContentCandidate Doc(Guid contact, string tax, DateTime date, decimal total)
        => new(Guid.NewGuid(), "EXP-0001", date, total, contact, tax);

    [Fact]
    public void CL4_ผู้ขายเดียวกัน_วันเดียวกัน_ยอดตรง_เตือนซ้ำพร้อมทางยืนยัน()
    {
        var hit = OcrDuplicateScanRule.DecideByContent(new[] { Doc(Vendor, "", Day, 1284.00m) }, 1284.00m, Day.AddHours(10), null, Vendor);
        Assert.NotNull(hit);
        Assert.Contains("ยืนยันสร้างซ้ำ", OcrDuplicateScanRule.ContentDuplicateMessage(hit!));   // หน้าสแกนเดิมจับจากคำนี้
        Assert.NotNull(OcrDuplicateScanRule.DecideByContent(new[] { Doc(Guid.NewGuid(), "0105548012345", Day, 80m) }, 80m, Day, "0105548012345", null));
    }

    [Fact]
    public void CL4_ทิศตรงข้าม_ไม่รู้ผู้ขาย_ผู้ขายอื่น_วันต่าง_ยอดต่าง_ไม่เตือน()
    {
        var docs = new[] { Doc(Vendor, "0105548012345", Day, 65.00m) };
        Assert.Null(OcrDuplicateScanRule.DecideByContent(docs, 65.00m, Day, null, null));                    // ไม่รู้ผู้ขาย (ร้านกาแฟ 65 บาททุกวัน)
        Assert.Null(OcrDuplicateScanRule.DecideByContent(docs, 65.00m, Day, "0105536092641", Guid.NewGuid())); // ผู้ขายอื่น
        Assert.Null(OcrDuplicateScanRule.DecideByContent(docs, 65.00m, Day.AddDays(1), null, Vendor));        // วันต่าง
        Assert.Null(OcrDuplicateScanRule.DecideByContent(docs, 65.50m, Day, null, Vendor));                   // ยอดต่าง
        Assert.Null(OcrDuplicateScanRule.DecideByContent(docs, null, Day, null, Vendor));                     // อ่านยอดไม่ได้
    }

    // ───── ฝ่ายค้าน 259-D L1/L2 ─────

    [Fact]
    public void L1_ยอดใหม่ต่ำกว่าVATเดิม_ไม่ถอด_ฐานไม่ติดลบ()
    {
        var (sub, vat, note) = OcrHeaderAmounts.RederiveAfterTotalEdit(null, 700m, 1000m, 500m);
        Assert.Null(sub);
        Assert.Equal(700m, vat);
        Assert.Contains("ต่ำกว่า VAT", note);
        // ทิศตรงข้าม: ยอดใหม่สูงกว่า VAT ยังถอดตามกติกาเดิม
        Assert.Equal(500m, OcrHeaderAmounts.RederiveAfterTotalEdit(467.29m, 32.71m, 500m, 535m).SubTotal);
    }

    [Fact]
    public void L2_บรรทัดเดียวตามยอดใหม่_ตามฐานที่บรรทัดถือ_ไม่ตรงไม่เดา()
    {
        Assert.Equal(500m, OcrHeaderAmounts.RescaleSingleLine(467.29m, 467.29m, 500m, 500m, 535m));   // บรรทัดเป็นฐาน
        Assert.Equal(535m, OcrHeaderAmounts.RescaleSingleLine(500m, 467.29m, 500m, 500m, 535m));      // บรรทัดเป็นยอดรวม
        Assert.Null(OcrHeaderAmounts.RescaleSingleLine(320m, 467.29m, 500m, 500m, 535m));             // ไม่ตรงทั้งสอง
    }
}
