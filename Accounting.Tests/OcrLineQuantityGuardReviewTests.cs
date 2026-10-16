using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ฝ่ายค้าน 73573e9a..658e3705 (ทีม OCR 6 · 2026-10-16) — ตัวกันจำนวนระเบิด <see cref="OcrLineQuantityGuard"/>**
///
/// <para>P1-1: รุ่นแรกตีตัวเลขใดก็ได้ที่ลงตัวเป็นส่วนลด (Note = null ⇒ อนุมัติเองได้) — คอลัมน์หัก ณ ที่จ่าย 3%/5% · "หักมัดจำ 30,000" (มัดจำไม่ถูกล้าง) ·
/// รหัสสินค้า "SKU 50" · อัตรา VAT ไม่มี % · P2-2 แท็กค้าง · P2-3 บล็อกที่ไม่จำเป็น · P2-4 เลขลำดับแถว · P2-5 บรรทัดไม่ลงตัว · P2-6 ผู้ใช้แก้แล้วถูกแก้กลับ</para>
///
/// <para>ทุกข้อมีทั้งทิศ "ใบที่พังกลับมาถูก" และ "ใบที่ถูกอยู่แล้วไม่ถูกแตะ" · ยอดหลังบันทึกคิดผ่าน <see cref="DocumentService.PreviewTotals"/> (สูตรจริง)</para>
/// </summary>
public class OcrLineQuantityGuardReviewTests
{
    private static OcrLinePaper Paper(string text) => OcrLineQuantityGuard.Read(text);

    private const string DiscountHeader = "รายการ จำนวน ราคา/หน่วย ส่วนลด จำนวนเงิน\n";
    private const string WhtHeader = "รายการ จำนวน ราคา/หน่วย หัก ณ ที่จ่าย ยอดสุทธิ\n";

    // ── P1-1 (b): % ที่เท่าอัตราหัก ณ ที่จ่าย ─────────────────────────────────────────────────────────────

    [Fact]
    public void P1_1_คอลัมน์3เปอร์เซ็นต์ไม่มีหัวคอลัมน์_ไม่ใช่ส่วนลดจากกระดาษ_บล็อก()
    {
        // เดิม: PrintedDiscount 300 + Note null (อนุมัติเองได้)
        var d = OcrLineQuantityGuard.Decide(1m, 10000m, 9700m, null, Paper("ค่าบริการ 1 10,000.00 3% 9,700.00"));
        Assert.NotEqual(OcrLineQuantityAction.PrintedDiscount, d.Action);
        Assert.Equal(OcrLineQuantityAction.UnevidencedDiscount, d.Action);
        Assert.True(d.Blocking);
        Assert.Contains("อัตราภาษีหัก ณ ที่จ่าย", d.Note);
        Assert.False(OcrPostingReadiness.Evaluate(d.Note, hasUsableDate: true).CanAutoApprove);

        var rent = OcrLineQuantityGuard.Decide(1m, 20000m, 19000m, null, Paper("ค่าเช่า 1 20,000.00 5% 19,000.00"));
        Assert.Equal(OcrLineQuantityAction.UnevidencedDiscount, rent.Action);
        Assert.True(rent.Blocking);
    }

    [Fact]
    public void P1_1_หัวคอลัมน์หักณที่จ่าย_ส่วนต่างเป็นภาษีหัก_ยอดบรรทัดก่อนหัก_ไม่มีส่วนลด_บล็อก()
    {
        var d = OcrLineQuantityGuard.Decide(1m, 10000m, 9700m, null, Paper(WhtHeader + "ค่าบริการ 1 10,000.00 3% 9,700.00"));
        Assert.Equal(OcrLineQuantityAction.DeductionRow, d.Action);
        Assert.Equal(10000m, d.Amount);      // จำนวน × ราคาที่พิมพ์ (ภาษีหัก ณ ที่จ่ายไม่ลดมูลค่ารายการ)
        Assert.Null(d.LineDiscount);
        Assert.Null(d.Quantity);
        Assert.True(d.Blocking);
        Assert.Contains("ภาษีหัก ณ ที่จ่าย", d.Note);
        // ป้ายบนแถวเอง ("หัก ณ ที่จ่าย 3%") ก็พอ
        var labelled = OcrLineQuantityGuard.Decide(1m, 10000m, 9700m, null, Paper("ค่าบริการ 1 10,000.00 หัก ณ ที่จ่าย 3% 9,700.00"));
        Assert.Equal(OcrLineQuantityAction.DeductionRow, labelled.Action);
    }

    [Fact]
    public void P1_1_ทิศตรงข้าม_หัวคอลัมน์ส่วนลด_3เปอร์เซ็นต์เป็นส่วนลดจริง_แจ้งไม่บล็อก()
    {
        var d = OcrLineQuantityGuard.Decide(1m, 10000m, 9700m, null, Paper(DiscountHeader + "ค่าบริการ 1 10,000.00 3% 9,700.00"));
        Assert.Equal(OcrLineQuantityAction.PrintedDiscount, d.Action);
        Assert.Equal(300m, d.LineDiscount);
        Assert.False(d.Blocking);
        Assert.StartsWith(OcrLineQuantityGuard.NoteTag, d.Note);
        // 10% (เท่าอัตราปันผล) — มีหัวคอลัมน์ส่วนลด ⇒ ส่วนลด · ไม่มี ⇒ ไม่รู้
        Assert.Equal(OcrLineQuantityAction.PrintedDiscount,
            OcrLineQuantityGuard.Decide(2m, 500m, 900m, null, Paper(DiscountHeader + "ถังน้ำ 2 ใบ 500.00 10% 900.00")).Action);
        Assert.Equal(OcrLineQuantityAction.UnevidencedDiscount,
            OcrLineQuantityGuard.Decide(2m, 500m, 900m, null, Paper("ถังน้ำ 2 ใบ 500.00 10% 900.00")).Action);
    }

    [Fact]
    public void P1_1_หัวคอลัมน์มีทั้งส่วนลดและหักณที่จ่าย_ตัดสินไม่ได้_บล็อก()
    {
        var d = OcrLineQuantityGuard.Decide(1m, 10000m, 9700m, null,
            Paper("รายการ จำนวน ราคา ส่วนลด หัก ณ ที่จ่าย จำนวนเงิน\nค่าบริการ 1 10,000.00 3% 9,700.00"));
        Assert.Equal(OcrLineQuantityAction.UnevidencedDiscount, d.Action);
        Assert.True(d.Blocking);
    }

    // ── P1-1 (a): มัดจำ/งวดก่อน ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_1_หักมัดจำบนแถว_ไม่ใช่ส่วนลด_ยอดบรรทัด100000ก่อนหัก_ให้วงจรมัดจำล้าง_บล็อก()
    {
        // เดิม: PrintedDiscount 30,000 (ลดราคาถาวร · บัญชีมัดจำ 11810 ไม่ถูกล้าง) + อนุมัติเองได้
        var d = OcrLineQuantityGuard.Decide(1m, 100000m, 70000m, null, Paper("งวดที่ 2 1 100,000.00 หักมัดจำ 30,000.00 70,000.00"));
        Assert.Equal(OcrLineQuantityAction.DeductionRow, d.Action);
        Assert.Equal(100000m, d.Amount);
        Assert.Null(d.LineDiscount);
        Assert.True(d.Blocking);
        Assert.Contains("มัดจำ", d.Note);
        Assert.Contains("30,000.00", d.Note);
        // "advance"/"งวดก่อน" ก็เป็นการหัก ไม่ใช่ส่วนลด
        Assert.Equal(OcrLineQuantityAction.DeductionRow,
            OcrLineQuantityGuard.Decide(1m, 100000m, 70000m, null, Paper("Installment 2 1 100,000.00 less advance 30,000.00 70,000.00")).Action);
        Assert.Equal(OcrLineQuantityAction.DeductionRow,
            OcrLineQuantityGuard.Decide(1m, 100000m, 70000m, null, Paper("งวดที่ 2 1 100,000.00 หักงวดก่อน 30,000.00 70,000.00")).Action);
    }

    [Fact]
    public void P1_1_หัวคอลัมน์มัดจำ_จำนวนพิมพ์บนแถว_ส่วนต่างไม่มีตัวเลขอธิบาย_ห้ามเป็นส่วนลด()
    {
        var d = OcrLineQuantityGuard.Decide(2m, 100m, 150m, null,
            Paper("รายการ จำนวน ราคา หักเงินมัดจำ ยอดสุทธิ\nสายยาง 2 ม้วน 100.00 150.00"));
        Assert.Equal(OcrLineQuantityAction.DeductionRow, d.Action);
        Assert.Equal(200m, d.Amount);
        Assert.Null(d.LineDiscount);
    }

    // ── P1-1 (c): ตัวเลขเปล่า ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_1_รหัสสินค้าSKU50_และอัตราVATไม่มีเปอร์เซ็นต์_ไม่ใช่ส่วนลดจากกระดาษ_บล็อก()
    {
        var sku = OcrLineQuantityGuard.Decide(1m, 500m, 450m, null, Paper("สินค้า 1 500.00 SKU 50 450.00"));
        Assert.Equal(OcrLineQuantityAction.UnevidencedDiscount, sku.Action);
        Assert.True(sku.Blocking);
        Assert.Equal(50m, sku.LineDiscount);   // บรรทัดลงตัว ยอดตรงกระดาษ — แต่ต้องมีคนดู

        var vat = OcrLineQuantityGuard.Decide(1m, 1000m, 993m, null, Paper("1,000.00 7 993.00"));
        Assert.NotEqual(OcrLineQuantityAction.PrintedDiscount, vat.Action);
        Assert.True(vat.Blocking);
    }

    [Fact]
    public void P1_1_ทิศตรงข้าม_ป้ายส่วนลดติดตัวเลขบนแถว_เป็นส่วนลดแม้ไม่มีหัวคอลัมน์()
    {
        var d = OcrLineQuantityGuard.Decide(1m, 500m, 450m, null, Paper("สินค้า 1 500.00 ส่วนลด 50.00 450.00"));
        Assert.Equal(OcrLineQuantityAction.PrintedDiscount, d.Action);
        Assert.Equal(50m, d.LineDiscount);
        Assert.False(d.Blocking);
        Assert.Equal(OcrLineQuantityAction.PrintedDiscount,
            OcrLineQuantityGuard.Decide(1m, 500m, 450m, null, Paper("Item 1 500.00 disc 50.00 450.00")).Action);
        // หัวกระดาษ "ใบลดหนี้" ไม่ใช่หัวคอลัมน์ส่วนลด ("ลด" ของ "ลดหนี้")
        Assert.False(Paper("ใบลดหนี้ รายการ จำนวน ราคา จำนวนเงิน\nสินค้า 1 500.00 50 450.00").HeaderHasDiscount);
    }

    // ── P1-1 (d): หมายเหตุแจ้งไม่บล็อก ───────────────────────────────────────────────────────────────

    [Fact]
    public void P1_1_หมายเหตุแจ้งNoteTag_ไม่ถูกนับเป็นแท็กบล็อก_แต่LINEQTYยังบล็อก()
    {
        Assert.DoesNotContain(OcrLineQuantityGuard.Tag, OcrLineQuantityGuard.NoteTag, StringComparison.Ordinal);
        Assert.True(OcrPostingReadiness.Evaluate(OcrLineQuantityGuard.NoteTag + " ส่วนลดรายบรรทัด 293.00 จากกระดาษ", hasUsableDate: true).CanAutoApprove);
        Assert.False(OcrPostingReadiness.Evaluate(OcrLineQuantityGuard.Tag + " x", hasUsableDate: true).CanAutoApprove);
    }

    // ── P2-4: เลขลำดับแถว ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P2_4_เลขลำดับแถว1ไม่ใช่จำนวน_ชั่งน้ำหนักได้0_45เหมือนเดิม()
    {
        // เดิม: เลข 1 ต้นแถวถูกนับเป็นจำนวนที่พิมพ์ ⇒ KeepPrintedGap (1 × 120 ≠ 54) · คำตอบเก่า 0.45 กก. ถูก
        var d = OcrLineQuantityGuard.Decide(1m, 120m, 54m, null, Paper("1 หมูสับ 120.00 54.00\n2 ไข่ไก่ 10 ฟอง 4.00 40.00"));
        Assert.Equal(OcrLineQuantityAction.FixQuantity, d.Action);
        Assert.Equal(0.450m, d.Quantity);
        Assert.True(d.Blocking);
        var nums = OcrLineQuantityGuard.Read("1 หมูสับ 120.00 54.00\n2 ไข่ไก่ 10 ฟอง 4.00 40.00").Numbers;
        Assert.True(nums[0].RowIndexLike);    // "1 หมูสับ"
        Assert.True(nums[3].RowIndexLike);    // "2 ไข่ไก่"
        Assert.False(nums[4].RowIndexLike);   // "10 ฟอง"
        Assert.True(nums[4].UnitAfter);
    }

    [Fact]
    public void P2_4_ทิศตรงข้าม_จำนวนต้นบรรทัดแบบเซลล์ของengine_และจำนวนคู่หน่วย_ยังเป็นจำนวน()
    {
        // แบบ Azure: แต่ละช่องอยู่คนละบรรทัด — "1.00" ต้นบรรทัดแต่ไม่มีข้อความตามบนบรรทัดเดียวกัน ⇒ จำนวน (ไม่ใช่เลขลำดับ)
        var cells = OcrLineQuantityGuard.Decide(2m, 100m, 150m, null, Paper("สายยาง\n2\nม้วน\n100.00\n150.00"));
        Assert.Equal(OcrLineQuantityAction.KeepPrintedGap, cells.Action);
        // "2 ด้าม" ต้นบรรทัด — มีหน่วยตาม ⇒ จำนวน
        var nums = OcrLineQuantityGuard.Read("2 ด้าม 10.00 20.00").Numbers;
        Assert.False(nums[0].RowIndexLike);
        Assert.True(nums[0].UnitAfter);
        // ตัวเลขก่อนราคาแต่มีคำที่ไม่ใช่หน่วยคั่น ⇒ ไม่ติดกัน ⇒ ไม่นับเป็นจำนวนที่พิมพ์
        Assert.Equal(OcrLineQuantityAction.FixQuantity,
            OcrLineQuantityGuard.Decide(1m, 120m, 54m, null, Paper("ล็อต 1 หมูสับบด 120.00 54.00")).Action);
    }

    // ── P2-5: ห้ามเก็บบรรทัดที่ไม่ลงตัว · สร้าง → แก้ → บันทึก ยอดไม่เลื่อน ───────────────────────────────

    private static DocumentLineRequest L(decimal q, decimal up, decimal disc, decimal vatRate = 7m)
        => new(Description: "x", Quantity: q, Unit: "ชิ้น", UnitPrice: up, DiscountPercent: 0m, VatRate: vatRate,
            WithholdingTaxRate: 0m, AccountId: null, DiscountAmount: disc);

    [Fact]
    public void P2_5_KeepPrintedGap_บรรทัดลงตัว_สร้างแก้บันทึกยอดตรงกระดาษไม่เลื่อน()
    {
        var data = new OcrExtractedData { SubTotal = 150m, VatAmount = 10.50m, TotalAmount = 160.50m };
        data.Items.Add(new OcrExtractedLineItem { Description = "สายยาง", Quantity = 2m, UnitPrice = 100m, Amount = 150m });
        const string paper = "ใบเสร็จรับเงิน/ใบกำกับภาษี\nสายยาง 2 ม้วน 100.00 150.00\nรวมเป็นเงิน 150.00\nภาษีมูลค่าเพิ่ม 7% 10.50\nรวมทั้งสิ้น 160.50";
        OcrService.SanitizeVatSplitArtifacts(data, false, paper);
        var it = Assert.Single(data.Items);
        Assert.Equal(2m, it.Quantity);
        Assert.Equal(100m, it.UnitPrice);
        Assert.Equal(150m, it.Amount);
        Assert.Equal(50m, it.LineDiscountAmount);
        Assert.Contains(data.ReasoningTrace, t => t.StartsWith(OcrLineQuantityGuard.Tag, StringComparison.Ordinal));

        // "สร้าง" = บรรทัดที่ตัวสร้างลง (จำนวน · ราคา · ส่วนลดของบรรทัด) · "แก้แล้วบันทึก" = ComputeLineAmounts ตัวจริงคิดใหม่จากช่องเดียวกัน
        var created = DocumentService.PreviewTotals(new[] { L(it.Quantity!.Value, it.UnitPrice!.Value,
            OcrEtaxLineNormalizer.ProvenLineDiscount(it.Quantity, it.UnitPrice, it.Amount, it.LineDiscountAmount)) }, false, 0m);
        Assert.Equal(150m, created.Net);
        Assert.Equal(10.50m, created.Vat);
        Assert.Equal(160.50m, created.Total);
        var resaved = DocumentService.PreviewTotals(new[] { L(2m, 100m, 50m) }, false, 0m);
        Assert.Equal(created.Total, resaved.Total);
        // ก่อนแก้: เก็บ 2 × 100 ส่วนลด 0 ยอด 150 ⇒ บันทึกซ้ำได้ 200 + VAT (เลื่อน 53.50)
        Assert.Equal(214.00m, DocumentService.PreviewTotals(new[] { L(2m, 100m, 0m) }, false, 0m).Total);

        // สร้างซ้ำจาก JSON เดิม (Sanitize รอบสอง) = ไม่แตะ ไม่มีหมายเหตุใหม่ (idempotent)
        var again = new OcrExtractedData();
        again.Items.Add(it);
        OcrService.SanitizeVatSplitArtifacts(again, false, paper);
        Assert.Equal(50m, again.Items[0].LineDiscountAmount);
        Assert.Empty(again.ReasoningTrace);
    }

    [Fact]
    public void P2_5_ส่วนต่างติดลบ_ยอดมากกว่าจำนวนคูณราคา_ลง1คูณยอด_จำนวนราคาที่พิมพ์อยู่ในคำอธิบาย_ยอดตรงกระดาษ()
    {
        // ทีม OCR 8 (2026-10-16): เดิมตั้งราคา = ยอด ÷ จำนวน (125 · ราคาที่กระดาษไม่พิมพ์ — ใบที่ไม่ลงตัวพอดีได้ 4 ตำแหน่ง ⇒ บันทึกซ้ำเลื่อน)
        // ตอนนี้ 1 × ยอด · จำนวน/ราคาที่พิมพ์อยู่ในคำอธิบาย · ยังบล็อก (ส่วนต่างที่กระดาษไม่บอก)
        var d = OcrLineQuantityGuard.Decide(2m, 100m, 250m, null, Paper("สายยาง 2 ม้วน 100.00 250.00"), "สายยาง");
        Assert.Equal(OcrLineQuantityAction.KeepPrintedGap, d.Action);
        Assert.Equal(1m, d.Quantity);
        Assert.Equal(250m, d.UnitPrice);
        Assert.Equal("สายยาง (จำนวน 2 × 100.00)", d.Description);
        Assert.Equal(OcrLineQuantityGuard.LumpSumUnit, d.Unit);
        Assert.Null(d.LineDiscount);
        Assert.True(d.Blocking);
        Assert.Contains("⟦1×250⟧", d.Note);
        Assert.Equal(250m, DocumentService.PreviewTotals(new[] { L(d.Quantity!.Value, d.UnitPrice!.Value, 0m, 0m) }, false, 0m).Total);
        // ใบที่ไม่ลงตัวพอดี (3 × 80 ≠ 250): เดิม 83.3333 ⇒ บันทึกซ้ำ 3 × 83.33 = 249.99 · ตอนนี้ 1 × 250 ทุกสตางค์
        var odd = OcrLineQuantityGuard.Decide(3m, 80m, 250m, null, Paper("ถุงปุ๋ย 3 ถุง 80.00 250.00"), "ถุงปุ๋ย");
        Assert.Equal((1m, 250m), (odd.Quantity!.Value, odd.UnitPrice!.Value));
        Assert.Equal(250m, DocumentService.PreviewTotals(new[] { L(odd.Quantity.Value, odd.UnitPrice.Value, 0m, 0m) }, false, 0m).Total);
    }

    [Fact]
    public void P2_5_DeductionRow_บรรทัดลงตัว_จำนวนคูณราคา_เท่ายอด()
    {
        var data = new OcrExtractedData();
        data.Items.Add(new OcrExtractedLineItem { Description = "งวดที่ 2", Quantity = 1m, UnitPrice = 100000m, Amount = 70000m });
        OcrService.SanitizeVatSplitArtifacts(data, false, "งวดที่ 2 1 100,000.00 หักมัดจำ 30,000.00 70,000.00");
        Assert.Equal(100000m, data.Items[0].Amount);
        Assert.Null(data.Items[0].LineDiscountAmount);
        Assert.Contains(data.ReasoningTrace, t => t.StartsWith(OcrLineQuantityGuard.Tag, StringComparison.Ordinal) && t.Contains("มัดจำ"));
    }

    // ── P2-2: ตรวจสดบนบรรทัดเอกสาร ────────────────────────────────────────────────────────────────────

    private static string FixQtyNote()
        => OcrLineQuantityGuard.Decide(1m, 120m, 54m, null, Paper("หมูสับ (ราคาต่อ กก.) 120.00 54.00")).Note!;

    [Fact]
    public void P2_2_บรรทัดยังถือค่าที่ระบบตั้ง_ยังบล็อก_แม้บรรทัดลงตัว()
    {
        var notes = "[Σ] ok\n" + FixQtyNote();
        var live = new[] { new OcrLiveLine(0.45m, 120m, 0m, 0m, 54m, 0m) };   // ตัวสร้างลงตามค่าที่ระบบตั้ง — ลงตัวโดยการสร้าง ไม่ใช่เพราะมีคนดู
        var after = OcrLineQuantityGuard.LiveNotes(notes, live, false, checkedLive: true);
        Assert.Equal(notes, after);
        Assert.False(OcrPostingReadiness.Evaluate(after, hasUsableDate: true).CanAutoApprove);
    }

    [Fact]
    public void P2_2_คนแก้บรรทัดแล้วและทุกบรรทัดลงตัว_ปลดแท็ก_อนุมัติเองได้_ข้ออื่นคง()
    {
        var notes = "[Σ] ok\n" + FixQtyNote() + "\n" + OcrLineQuantityGuard.NoteTag + " ส่วนลดรายบรรทัด 1.00 จากกระดาษ";
        var live = new[] { new OcrLiveLine(0.5m, 108m, 0m, 0m, 54m, 0m) };   // คนแก้เป็น 0.5 กก. × 108
        var after = OcrLineQuantityGuard.LiveNotes(notes, live, false, checkedLive: true);
        Assert.DoesNotContain(OcrLineQuantityGuard.Tag + " ", after);
        Assert.Contains("[Σ] ok", after);
        Assert.Contains(OcrLineQuantityGuard.NoteTag, after);
        Assert.True(OcrPostingReadiness.Evaluate(after, hasUsableDate: true).CanAutoApprove);
    }

    [Fact]
    public void P2_2_ทิศปลอดภัย_แก้แล้วแต่ไม่ลงตัว_หรือตรวจสดไม่ได้_หรือแท็กเก่าไม่มีร่องรอย_บล็อกต่อ()
    {
        var notes = FixQtyNote();
        Assert.Equal(notes, OcrLineQuantityGuard.LiveNotes(notes, new[] { new OcrLiveLine(0.5m, 108m, 0m, 0m, 60m, 0m) }, false, true));
        Assert.Equal(notes, OcrLineQuantityGuard.LiveNotes(notes, new[] { new OcrLiveLine(0.5m, 108m, 0m, 0m, 54m, 0m) }, false, false));
        Assert.Equal(notes, OcrLineQuantityGuard.LiveNotes(notes, Array.Empty<OcrLiveLine>(), false, true));
        const string legacy = "[LINE-QTY] จำนวน 1 × ราคา 1,330.00 ≠ ยอดบรรทัด 1,037.00 — ระบบตั้งจำนวน = 0.78";   // รุ่น 658e3705 ไม่มีร่องรอย
        Assert.Equal(legacy, OcrLineQuantityGuard.LiveNotes(legacy, new[] { new OcrLiveLine(1m, 1330m, 0m, 293m, 1037m, 0m) }, false, true));
    }

    [Fact]
    public void P2_2_ราคาที่ระบบตั้ง4ตำแหน่ง_บรรทัดเอกสารเก็บ2ตำแหน่ง_ยังนับเป็นค่าของระบบ_บล็อกต่อ()
    {
        // แท็กรุ่นก่อนทีม OCR 8 (persist ใน ProcessingNotes): DocumentLine.UnitPrice = numeric(18,2) ⇒ 83.3333 ถูกเก็บเป็น 83.33 ·
        // 3 × 83.33 = 249.99 ≈ 250 (ลงตัวในเกณฑ์) — ถ้าเทียบราคาเป๊ะจะปลดแท็กทันทีที่สร้าง
        const string legacy = "[LINE-QTY] จำนวน 3 × ราคา 80.00 = 240.00 · ยอดบรรทัด 250.00 (ยอดมากกว่า 10.00) — ตั้งราคา 83.3333 · ค่าที่ระบบตั้ง ⟦3×83.3333⟧";
        var legacyLive = new[] { new OcrLiveLine(3m, 83.33m, 0m, 0m, 249.99m, 0m) };
        Assert.Equal(legacy, OcrLineQuantityGuard.LiveNotes(legacy, legacyLive, false, checkedLive: true));
        // รุ่นนี้: ระบบไม่ตั้งราคา 4 ตำแหน่งแล้ว — 1 × 250 (ร่องรอย ⟦1×250⟧) · บรรทัดที่ตัวสร้างลงตามค่านั้น = ยังไม่มีคนดู ⇒ บล็อกต่อ
        var note = OcrLineQuantityGuard.Decide(3m, 80m, 250m, null, Paper("ถุงปุ๋ย 3 ถุง 80.00 250.00")).Note!;
        Assert.Contains("⟦1×250⟧", note);
        Assert.Equal(note, OcrLineQuantityGuard.LiveNotes(note, new[] { new OcrLiveLine(1m, 250m, 0m, 0m, 250m, 0m) }, false, checkedLive: true));
    }

    [Fact]
    public void P2_2_ตรวจลงตัว_ราคารวมVAT_ถอดVATตามสูตรเอกสาร()
    {
        // พรามี่: 1 × 1,330 − 293 = 1,037 รวม VAT ⇒ Amount 969.16 · ตัวกันแบบ KeepPrintedGap ของ (2 × 100) ไม่อยู่แล้ว ⇒ ปลด
        var notes = OcrLineQuantityGuard.Decide(2m, 100m, 150m, null, Paper("สายยาง 2 ม้วน 100.00 150.00")).Note!;
        var live = new[] { new OcrLiveLine(1m, 1330m, 0m, 293m, 969.16m, 7m) };
        Assert.Equal("", OcrLineQuantityGuard.LiveNotes(notes, live, pricesIncludeVat: true, checkedLive: true));
        Assert.Equal(notes, OcrLineQuantityGuard.LiveNotes(notes, live, pricesIncludeVat: false, checkedLive: true));   // ไม่ถอด VAT ⇒ ไม่ลงตัว ⇒ คง
    }

    // ── P2-6: บรรทัดที่ผู้ใช้แก้ · ส่งหมายเหตุทุกเส้น ────────────────────────────────────────────────────

    [Fact]
    public void P2_6_บรรทัดที่ผู้ใช้แก้_ระบบไม่แก้ค่า_ไม่ลงตัวติดแท็ก_ลงตัวไม่มีหมายเหตุ()
    {
        var data = new OcrExtractedData();
        data.Items.Add(new OcrExtractedLineItem { Description = "a", Quantity = 1m, UnitPrice = 1330m, Amount = 1037m, UserEdited = true });
        data.Items.Add(new OcrExtractedLineItem { Description = "b", Quantity = 2m, UnitPrice = 10m, Amount = 20m, UserEdited = true });
        OcrService.SanitizeVatSplitArtifacts(data, false, null);
        Assert.Equal(1m, data.Items[0].Quantity);     // ไม่หาร 0.78 ทับค่าผู้ใช้
        Assert.Null(data.Items[0].LineDiscountAmount);
        var note = Assert.Single(data.ReasoningTrace, t => t.StartsWith(OcrLineQuantityGuard.Tag, StringComparison.Ordinal));
        Assert.Contains("ผู้ใช้แก้", note);
        // ทิศตรงข้าม: บรรทัดที่ไม่ได้แก้ยังเดินตัวกันตามเดิม
        var plain = new OcrExtractedData();
        plain.Items.Add(new OcrExtractedLineItem { Description = "a", Quantity = 1m, UnitPrice = 1330m, Amount = 1037m });
        OcrService.SanitizeVatSplitArtifacts(plain, false, null);
        Assert.Equal(0.780m, plain.Items[0].Quantity);
        // ธงอยู่รอดผ่าน JSON (ExtractedItemsJson)
        var json = OcrService.SerializeExtractedItems(data.Items.ToList());
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<List<OcrExtractedLineItem>>(json)![0].UserEdited);
    }

    [Fact]
    public void P2_6_MergeNotes_ต่อเฉพาะหมายเหตุของตัวกัน_ไม่ซ้ำ()
    {
        var trace = new[] { "[Reasoning] x", OcrLineQuantityGuard.Tag + " a", OcrLineQuantityGuard.NoteTag + " b", OcrLineQuantityGuard.Tag + " a" };
        var merged = OcrLineQuantityGuard.MergeNotes("[Σ] เดิม", trace);
        Assert.Equal("[Σ] เดิม\n" + OcrLineQuantityGuard.Tag + " a\n" + OcrLineQuantityGuard.NoteTag + " b", merged);
        Assert.Equal(merged, OcrLineQuantityGuard.MergeNotes(merged, trace));
        Assert.Null(OcrLineQuantityGuard.MergeNotes(null, new[] { "[Reasoning] x" }));
    }

    // ── เครื่องมือที่ใช้ร่วม (เจ้าของเดิม) ──────────────────────────────────────────────────────────────

    [Fact]
    public void ตัวแปลงPUAฟอนต์ไทย_และป้ายหักณที่จ่ายของPaperWhtReader()
    {
        Assert.Equal("แผ่นรองซับ ส่วนลด ผู้ขาย ไทยแลนด์ แฮปปี้ เปิด", ThaiTextNormalizer.MapThaiPua("แผ\uF70Aนรองซับ ส\uF70Aวนลด ผู\uF70Bขาย ไทยแลนด\uF70E แฮปป\uF702\uF714 เป\uF701ด"));
        Assert.Equal("abc", ThaiTextNormalizer.MapThaiPua("abc"));
        Assert.True(PaperWhtReader.HasLabel("หัก ณ ที่จ่าย 3%"));
        Assert.True(PaperWhtReader.HasLabel("WHT 3%"));
        Assert.False(PaperWhtReader.HasLabel("ส่วนลด 3%"));
        Assert.False(PaperWhtReader.HasLabel(null));
    }
}
