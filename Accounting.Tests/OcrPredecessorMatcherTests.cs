using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ล็อกกติกา "สแกนใบนี้ต่อเนื่องจากเอกสารใบไหน" — ตัวเดียวของทุกชนิดเอกสาร (PO→ใบซื้อ ·
/// ใบเสนอราคา→ใบแจ้งหนี้ · ใบแจ้งหนี้→ใบเสร็จ ฯลฯ). เคสตั้งต้นจากผู้ใช้: PO บุญทรัพย์ 599 บาท
/// กับใบแจ้งหนี้ซื้อบุญทรัพย์ 599 บาท ต้องผูกให้เอง · มีเลข PO บนกระดาษยิ่งชัวร์ · กำกวมต้องให้คนเลือก
/// </summary>
public class OcrPredecessorMatcherTests
{
    private static readonly Guid Po1 = Guid.NewGuid();
    private static readonly Guid Po2 = Guid.NewGuid();
    private static readonly Guid Po3 = Guid.NewGuid();

    private static PredecessorCandidate Po(Guid id, string no, decimal total, decimal? balance = null, params string[] lines)
        => new(id, DocumentType.PurchaseOrder, no, new DateTime(2026, 9, 1), total, balance ?? total, lines);

    private static PredecessorScanFacts Facts(string raw, decimal? total, decimal? sub = null, params string[] lines)
        => new(raw, total, sub, lines);

    [Fact]
    public void บุญทรัพย์_PO599_ใบแจ้งหนี้599_ใบเดียว_ต้องผูกอัตโนมัติด้วยยอดตรง()
    {
        var d = OcrPredecessorMatcher.Decide(
            Facts("บริษัท บุญทรัพย์ จำกัด ใบแจ้งหนี้ INV-889 รวมทั้งสิ้น 599.00", 599m),
            new[] { Po(Po1, "PO-2026-0012", 599m), Po(Po2, "PO-2026-0007", 1250m) });
        Assert.NotNull(d.AutoLink);
        Assert.Equal(Po1, d.AutoLink!.Candidate.Id);
        Assert.Equal(PredecessorMatchStrength.ExactTotal, d.AutoLink.Strength);
    }

    [Fact]
    public void เลขPOบนกระดาษ_ชนะยอด_และผูกอัตโนมัติแม้ยอดต่าง()
    {
        // วางบิลบางส่วน: กระดาษ 300 จาก PO 599 แต่พิมพ์เลข PO ชัด
        var d = OcrPredecessorMatcher.Decide(
            Facts("อ้างอิงใบสั่งซื้อ PO 2026 0012 ยอด 300.00", 300m),
            new[] { Po(Po1, "PO-2026-0012", 599m), Po(Po2, "PO-2026-0007", 300m) });
        Assert.NotNull(d.AutoLink);
        Assert.Equal(Po1, d.AutoLink!.Candidate.Id);
        Assert.Equal(PredecessorMatchStrength.ExplicitReference, d.AutoLink.Strength);
    }

    [Fact]
    public void ยอดตรงสองใบ_ห้ามเดา_ต้องให้คนเลือกทั้งสอง()
    {
        var d = OcrPredecessorMatcher.Decide(
            Facts("รวม 599.00", 599m),
            new[] { Po(Po1, "PO-2026-0012", 599m), Po(Po2, "PO-2026-0013", 599m) });
        Assert.Null(d.AutoLink);
        Assert.Equal(2, d.Candidates.Count);
        Assert.Contains("ให้ผู้ใช้เลือก", d.Summary);
    }

    [Fact]
    public void กระดาษอ้างเลขสองใบ_เป็นวางบิลรวม_ต้องให้คนเลือก()
    {
        var d = OcrPredecessorMatcher.Decide(
            Facts("ตาม PO-2026-0012 และ PO-2026-0013", 1198m),
            new[] { Po(Po1, "PO-2026-0012", 599m), Po(Po2, "PO-2026-0013", 599m) });
        Assert.Null(d.AutoLink);
        Assert.All(d.Candidates, c => Assert.Equal(PredecessorMatchStrength.ExplicitReference, c.Strength));
    }

    [Fact]
    public void ยอดตรงกับยอดค้าง_ของใบที่วางบิลไปบางส่วนแล้ว_นับว่าตรง()
    {
        var d = OcrPredecessorMatcher.Decide(
            Facts("รวม 299.00", 299m),
            new[] { Po(Po1, "PO-2026-0012", 599m, balance: 299m) });
        Assert.NotNull(d.AutoLink);
        Assert.Contains("ยอดค้าง", d.AutoLink!.Reason);
    }

    [Fact]
    public void รายการคล้ายอย่างเดียว_เสนอแต่ไม่ผูกเอง()
    {
        var d = OcrPredecessorMatcher.Decide(
            Facts("กระดาษ A4 80 แกรม 5 รีม · หมึกพิมพ์ HP 2 กล่อง รวม 2,480", 2480m, null, "กระดาษ A4 80 แกรม", "หมึกพิมพ์ HP"),
            new[] { Po(Po1, "PO-2026-0012", 2600m, null, "กระดาษ A4 80 แกรม Double A", "หมึกพิมพ์ HP 682") });
        Assert.Null(d.AutoLink);
        Assert.Single(d.Candidates);
        Assert.Equal(PredecessorMatchStrength.LineOverlap, d.Candidates[0].Strength);
    }

    [Fact]
    public void ไม่มีอะไรตรงเลย_ต้องไม่เสนอใบล่าสุดแทน()
    {
        var d = OcrPredecessorMatcher.Decide(
            Facts("ค่าบริการทำความสะอาด 3,500", 3500m, null, "ค่าบริการทำความสะอาด"),
            new[] { Po(Po1, "PO-2026-0012", 599m, null, "กระดาษ A4"), Po(Po2, "PO-2026-0013", 1250m, null, "หมึกพิมพ์") });
        Assert.Null(d.AutoLink);
        Assert.Empty(d.Candidates);
    }

    [Fact]
    public void เลขที่สั้นหรือตัวเลขล้วนสั้น_ไม่นับว่าอ้างอิง_กันชนยอดเงินบนกระดาษ()
    {
        // "0339" คือเลขเล่มใบเสร็จ — ตัวเลขล้วน 4 หลักโผล่ในยอดเงิน/วันที่ได้ทุกใบ
        Assert.False(OcrPredecessorMatcher.ReferenceAppears(OcrPredecessorMatcher.NormalizeForReference("รวม 10,339.00 วันที่ 03/09/2569"), "0339"));
        Assert.False(OcrPredecessorMatcher.ReferenceAppears(OcrPredecessorMatcher.NormalizeForReference("เลข 2026001"), "2026001"));
        Assert.True(OcrPredecessorMatcher.ReferenceAppears(OcrPredecessorMatcher.NormalizeForReference("ตามใบเสนอราคา QT-2026-00045"), "QT-2026-00045"));
        // OCR แยกช่องว่าง/ขีดเพี้ยน ยังต้องเจอ
        Assert.True(OcrPredecessorMatcher.ReferenceAppears(OcrPredecessorMatcher.NormalizeForReference("ตาม PO 2026 0012 ลงวันที่"), "PO-2026-0012"));
    }

    [Fact]
    public void ฝั่งขาย_ใบเสนอราคาเป็นต้นทางของใบแจ้งหนี้_กติกาเดียวกัน()
    {
        var qt = new PredecessorCandidate(Po3, DocumentType.Quotation, "QT-2026-00045", new DateTime(2026, 8, 20), 10700m, 10700m, new[] { "ออกแบบเว็บไซต์" });
        var d = OcrPredecessorMatcher.Decide(
            Facts("ใบแจ้งหนี้ ค่าออกแบบเว็บไซต์ 10,000 VAT 700 รวม 10,700", 10700m, 10000m, "ค่าออกแบบเว็บไซต์"),
            new[] { qt });
        Assert.NotNull(d.AutoLink);
        Assert.Equal(DocumentType.Quotation, d.AutoLink!.Candidate.Type);
    }

    [Fact]
    public void ยอดต่างเกิน1บาท_ไม่ถือว่าตรง()
    {
        var d = OcrPredecessorMatcher.Decide(Facts("รวม 601.00", 601m), new[] { Po(Po1, "PO-2026-0012", 599m) });
        Assert.Null(d.AutoLink);
        Assert.Empty(d.Candidates);
    }

    [Fact]
    public void ลำดับผู้สมัคร_เรียงตามคะแนน_แล้วใบใหม่กว่าอยู่บน()
    {
        var older = new PredecessorCandidate(Po1, DocumentType.PurchaseOrder, "PO-2026-0001", new DateTime(2026, 1, 1), 599m, 599m, Array.Empty<string>());
        var newer = new PredecessorCandidate(Po2, DocumentType.PurchaseOrder, "PO-2026-0090", new DateTime(2026, 9, 1), 599m, 599m, Array.Empty<string>());
        var d = OcrPredecessorMatcher.Decide(Facts("รวม 599", 599m), new[] { older, newer });
        Assert.Null(d.AutoLink);
        Assert.Equal(Po2, d.Candidates[0].Candidate.Id);
    }
}
