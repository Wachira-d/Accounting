using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กติกาการจัดหน้าเอกสารพิมพ์ — คอลัมน์ส่วนลดที่ว่างทั้งใบ + หัวกระดาษซ้ำทุกหน้า
///
/// <para>ตัวตัดสินใจ "โชว์คอลัมน์ส่วนลดไหม" เป็น resolver กลางตัวเดียว
/// (<see cref="PdfGenerationService.ShouldShowDiscountColumn"/>) ที่ renderer ทั้งสอง
/// ตัวเรียก — กฎเหล็ก #4 ข้อ "สอง renderer ห้าม drift" จะเป็นจริงได้ก็ต่อเมื่อ
/// ตรรกะอยู่ที่เดียว. เทสต์ชุดนี้ล็อกพฤติกรรมของ resolver ตัวนั้นไว้</para>
/// </summary>
public class DocumentPrintLayoutTests
{
    private static DocumentTemplate Tpl(bool showDiscount = true, bool hideEmpty = true) => new()
    {
        ShowDiscount = showDiscount,
        HideEmptyDiscountColumn = hideEmpty,
    };

    private static Document Doc(params DocumentLine[] lines)
    {
        var d = new Document { DocumentType = DocumentType.TaxInvoice };
        foreach (var l in lines) d.Lines.Add(l);
        return d;
    }

    private static DocumentLine Line(decimal discAmt = 0m, decimal discPct = 0m, bool deleted = false)
        => new()
        {
            Description = "สินค้า",
            Quantity = 1m,
            UnitPrice = 100m,
            DiscountAmount = discAmt,
            DiscountPercent = discPct,
            IsDeleted = deleted,
        };

    // ── ค่าปกติ: ไม่มีส่วนลดเลย → ซ่อนคอลัมน์ ────────────────────────────

    [Fact]
    public void ไม่มีส่วนลดเลย_ซ่อนคอลัมน์()
    {
        Assert.False(PdfGenerationService.ShouldShowDiscountColumn(
            Doc(Line(), Line(), Line()), Tpl()));
    }

    [Fact]
    public void มีส่วนลดบรรทัดเดียว_ยังต้องโชว์คอลัมน์()
    {
        // ถ้าซ่อนทั้งที่มีส่วนลดจริง ยอดบนใบจะอธิบายตัวเองไม่ได้
        // (qty × unit ≠ amount) — ผู้อ่านหาที่มาของผลต่างไม่เจอ
        Assert.True(PdfGenerationService.ShouldShowDiscountColumn(
            Doc(Line(), Line(discAmt: 50m), Line()), Tpl()));
    }

    [Fact]
    public void ส่วนลดเป็นเปอร์เซ็นต์_ก็นับว่ามีส่วนลด()
    {
        // ส่วนลด % ที่ยังไม่ถูก materialize เป็นจำนวนเงินก็ต้องมีคอลัมน์รองรับ
        Assert.True(PdfGenerationService.ShouldShowDiscountColumn(
            Doc(Line(discPct: 10m)), Tpl()));
    }

    [Fact]
    public void ส่วนลดเศษสตางค์ต่ำกว่าครึ่ง_ถือว่าไม่มี()
    {
        // 0.004 ปัดเป็น 0.00 บนใบ → คอลัมน์จะเป็นศูนย์ทั้งแถวอยู่ดี
        Assert.False(PdfGenerationService.ShouldShowDiscountColumn(
            Doc(Line(discAmt: 0.004m)), Tpl()));
    }

    [Fact]
    public void ส่วนลดหนึ่งสตางค์_ถือว่ามี()
    {
        Assert.True(PdfGenerationService.ShouldShowDiscountColumn(
            Doc(Line(discAmt: 0.01m)), Tpl()));
    }

    [Fact]
    public void บรรทัดที่ถูกลบ_ไม่นับ()
    {
        // บรรทัด IsDeleted ไม่ถูกพิมพ์ ส่วนลดของมันจึงไม่ทำให้คอลัมน์โผล่
        Assert.False(PdfGenerationService.ShouldShowDiscountColumn(
            Doc(Line(), Line(discAmt: 999m, deleted: true)), Tpl()));
    }

    [Fact]
    public void เอกสารไม่มีบรรทัดเลย_ซ่อนคอลัมน์()
    {
        Assert.False(PdfGenerationService.ShouldShowDiscountColumn(Doc(), Tpl()));
    }

    // ── ตั้งค่าเทมเพลตชนะ ────────────────────────────────────────────────

    [Fact]
    public void ปิดซ่อนอัตโนมัติ_คอลัมน์โผล่เสมอแม้ไม่มีส่วนลด()
    {
        // กิจการที่อยากให้ทุกใบหน้าตาเหมือนกันเป๊ะ ปิดตัวช่วยนี้ได้
        Assert.True(PdfGenerationService.ShouldShowDiscountColumn(
            Doc(Line(), Line()), Tpl(hideEmpty: false)));
    }

    [Fact]
    public void ปิดคอลัมน์ส่วนลดถาวร_ชนะทุกกรณี()
    {
        // ShowDiscount=false คือ "ไม่อยากให้มีคอลัมน์นี้เลย" — ต้องชนะแม้มี
        // ส่วนลดจริง มิฉะนั้นติ๊กปิดแล้วยังโผล่ = silent no-op
        Assert.False(PdfGenerationService.ShouldShowDiscountColumn(
            Doc(Line(discAmt: 500m)), Tpl(showDiscount: false)));
        Assert.False(PdfGenerationService.ShouldShowDiscountColumn(
            Doc(Line(discAmt: 500m)), Tpl(showDiscount: false, hideEmpty: false)));
    }

    // ── ค่าเริ่มต้นของเทมเพลตใหม่ ────────────────────────────────────────

    [Fact]
    public void เทมเพลตใหม่_ซ่อนคอลัมน์ว่างและพิมพ์หัวซ้ำทุกหน้าโดยปริยาย()
    {
        var t = new DocumentTemplate();
        Assert.True(t.HideEmptyDiscountColumn);
        Assert.True(t.RepeatHeaderEveryPage);
        // ShowDiscount ยังเป็น true — ตัวช่วยใหม่ไม่ได้แปลว่าปิดคอลัมน์ถาวร
        Assert.True(t.ShowDiscount);
    }
}
