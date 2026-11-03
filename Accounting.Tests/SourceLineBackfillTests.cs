using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 232 — ตัวตัดสินเดียวของการจับคู่บรรทัดใบลูก↔ใบต้นทาง "แบบแน่นอน" แล้วประทับ <c>SourceLineId</c>
/// (ที่มา: ป้ายใบสั่งซื้อ "ไม่ทราบสัดส่วน" ทั้งที่ PI ยอดเท่ากันเป๊ะ — บรรทัดใบลูกไม่ชี้บรรทัดต้นทาง)
/// สองครึ่ง: (ก) ใบที่จับคู่ได้แน่นอนต้องถูกประทับ (ข) ใบที่จับไม่ได้/มีค่าอยู่แล้ว ต้องไม่ถูกแตะ (ห้ามเดา)
/// </summary>
public class SourceLineBackfillTests
{
    private static DocumentLine L(int order, string desc, decimal qty, string? code = null, Guid? src = null) => new()
    {
        LineOrder = order, Description = desc, Quantity = qty, ProductCode = code, SourceLineId = src, UnitPrice = 100m, Amount = 100m * qty,
    };

    [Fact]
    public void จับคู่แน่นอน_จำนวนบรรทัดเท่า_คำอธิบายและจำนวนตรง_ประทับครบทุกบรรทัด()
    {
        var src = new List<DocumentLine> { L(1, "สว่านไฟฟ้า", 2m), L(2, "ค่าขนส่ง", 1m) };
        var child = new List<DocumentLine> { L(1, " สว่านไฟฟ้า ", 2m), L(2, "ค่าขนส่ง", 1m) };
        Assert.Equal(2, SourceLineBackfill.Stamp(child, src, matchQuantity: true));
        Assert.Equal(src[0].Id, child[0].SourceLineId);
        Assert.Equal(src[1].Id, child[1].SourceLineId);
    }

    [Fact]
    public void รหัสสินค้าชนะคำอธิบาย_และไม่สนตัวพิมพ์()
    {
        var src = new List<DocumentLine> { L(1, "ชื่อบนใบสั่งซื้อ", 5m, "p-01") };
        var child = new List<DocumentLine> { L(1, "ชื่อบนใบกำกับผู้ขาย", 5m, "P-01") };
        Assert.Equal(1, SourceLineBackfill.Stamp(child, src, matchQuantity: true));
        Assert.Equal(src[0].Id, child[0].SourceLineId);
    }

    [Fact]
    public void ทิศตรงข้าม_จำนวนไม่ตรงบรรทัดเดียว_ไม่แตะทั้งใบ()
    {
        var src = new List<DocumentLine> { L(1, "A", 2m), L(2, "B", 1m) };
        var child = new List<DocumentLine> { L(1, "A", 2m), L(2, "B", 3m) };
        Assert.Equal(0, SourceLineBackfill.Stamp(child, src, matchQuantity: true));
        Assert.All(child, l => Assert.Null(l.SourceLineId));
    }

    [Fact]
    public void ทิศตรงข้าม_จำนวนบรรทัดไม่เท่า_หรือคำอธิบายไม่ตรง_ไม่แตะ()
    {
        var src = new List<DocumentLine> { L(1, "A", 1m), L(2, "B", 1m) };
        Assert.Equal(0, SourceLineBackfill.Stamp(new List<DocumentLine> { L(1, "A", 1m) }, src, true));
        Assert.Equal(0, SourceLineBackfill.Stamp(new List<DocumentLine> { L(1, "A", 1m), L(2, "C", 1m) }, src, true));
        // ฝั่งหนึ่งมีรหัสสินค้า อีกฝั่งไม่มี = ไม่เดาจากคำอธิบาย
        Assert.Equal(0, SourceLineBackfill.Stamp(new List<DocumentLine> { L(1, "A", 1m, "X"), L(2, "B", 1m) }, src, true));
        Assert.Equal(0, SourceLineBackfill.Stamp(new List<DocumentLine>(), src, true));   // ชุดว่าง = ไม่มีอะไรให้จับ
    }

    [Fact]
    public void ทิศตรงข้าม_บรรทัดชี้ต้นทางนี้อยู่แล้ว_ไม่ทับ()
    {
        var src = new List<DocumentLine> { L(1, "A", 1m), L(2, "B", 1m) };
        // ใบที่แปลงมาจริง: บรรทัดแรกชี้ src[1] (ผู้ใช้สลับเอง) — ค่าที่มีอยู่ชนะ ห้ามเปลี่ยนเป็น src[0]
        var child = new List<DocumentLine> { L(1, "A", 1m, src: src[1].Id), L(2, "B", 1m) };
        Assert.Equal(0, SourceLineBackfill.Stamp(child, src, true));
        Assert.Equal(src[1].Id, child[0].SourceLineId);
        Assert.Null(child[1].SourceLineId);
    }

    [Fact]
    public void บรรทัดชี้ที่อื่น_ทับได้เฉพาะเมื่ออนุญาต_overwriteForeign()
    {
        var src = new List<DocumentLine> { L(1, "A", 1m) };
        var dangling = Guid.NewGuid();
        var child = new List<DocumentLine> { L(1, "A", 1m, src: dangling) };
        Assert.Equal(0, SourceLineBackfill.Stamp(child, src, true));
        Assert.Equal(dangling, child[0].SourceLineId);
        Assert.Equal(1, SourceLineBackfill.Stamp(child, src, true, overwriteForeign: true));
        Assert.Equal(src[0].Id, child[0].SourceLineId);
    }

    [Fact]
    public void แก้ใบต้นทาง_บรรทัดถูกสร้างใหม่_ตัวชี้ของใบลูกย้ายตาม_แม้จำนวนเปลี่ยน()
    {
        var prev = new List<DocumentLine> { L(1, "A", 2m), L(2, "B", 1m) };
        var fresh = new List<DocumentLine> { L(1, "A", 3m), L(2, "B", 1m) };   // ผู้ใช้แก้จำนวนบรรทัดแรก (เหตุผลที่แก้ใบ)
        var dependents = new List<DocumentLine> { L(1, "A", 2m, src: prev[0].Id), L(2, "B", 1m, src: prev[1].Id) };
        Assert.Equal(2, SourceLineBackfill.Remap(dependents, prev, fresh));
        Assert.Equal(fresh[0].Id, dependents[0].SourceLineId);
        Assert.Equal(fresh[1].Id, dependents[1].SourceLineId);
    }

    [Fact]
    public void ทิศตรงข้าม_แก้ใบต้นทางแล้วลบบรรทัดหนึ่ง_จับไม่แน่นอน_ไม่ย้าย()
    {
        var prev = new List<DocumentLine> { L(1, "A", 2m), L(2, "B", 1m) };
        var fresh = new List<DocumentLine> { L(1, "B", 1m) };
        var dependents = new List<DocumentLine> { L(1, "A", 2m, src: prev[0].Id) };
        Assert.Equal(0, SourceLineBackfill.Remap(dependents, prev, fresh));
        Assert.Equal(prev[0].Id, dependents[0].SourceLineId);   // คง dangling (ป้ายไม่ทราบสัดส่วน) ดีกว่าชี้ผิดบรรทัด
    }

    [Fact]
    public void ฝ่ายค้านS3_คำอธิบายซ้ำสองบรรทัดสลับจำนวน_Remap_ไม่ย้ายตัวชี้ผิด_แต่จำนวนแยกได้ก็ย้าย()
    {
        // ใบสั่งซื้อ "บริการ" 2 บรรทัด (จำนวน 2 และ 5) · ผู้ใช้แก้ใบแล้วสลับแถว — ลำดับบรรทัดอย่างเดียวจะย้ายตัวชี้ไขว้
        var prev = new List<DocumentLine> { L(1, "บริการ", 2m), L(2, "บริการ", 5m) };
        var swapped = new List<DocumentLine> { L(1, "บริการ", 5m), L(2, "บริการ", 2m) };
        var dependents = new List<DocumentLine> { L(1, "บริการ", 2m, src: prev[0].Id), L(2, "บริการ", 5m, src: prev[1].Id) };
        // กุญแจซ้ำ ⇒ บังคับเทียบจำนวน ⇒ คู่ตามลำดับจำนวนไม่ตรง ⇒ ไม่ย้าย (คง dangling ดีกว่าชี้ผิด)
        Assert.Equal(0, SourceLineBackfill.Remap(dependents, prev, swapped));
        Assert.Equal(prev[0].Id, dependents[0].SourceLineId);
        Assert.Equal(prev[1].Id, dependents[1].SourceLineId);
        // ลำดับเดิมจำนวนเดิม ⇒ จำนวนแยกได้ ⇒ ย้ายถูกคู่
        var sameOrder = new List<DocumentLine> { L(1, "บริการ", 2m), L(2, "บริการ", 5m) };
        Assert.Equal(2, SourceLineBackfill.Remap(dependents, prev, sameOrder));
        Assert.Equal(sameOrder[0].Id, dependents[0].SourceLineId);
        Assert.Equal(sameOrder[1].Id, dependents[1].SourceLineId);
    }

    [Fact]
    public void ฝ่ายค้านS3_กุญแจและจำนวนซ้ำกันทั้งคู่_ไม่จับเลย()
    {
        var src = new List<DocumentLine> { L(1, "บริการ", 1m), L(2, "บริการ", 1m) };
        var child = new List<DocumentLine> { L(1, "บริการ", 1m), L(2, "บริการ", 1m) };
        Assert.Equal(0, SourceLineBackfill.Stamp(child, src, matchQuantity: true));
        Assert.All(child, l => Assert.Null(l.SourceLineId));
    }

    [Fact]
    public void ฝ่ายค้านS5_แท็กผลจับคู่ของสแกน_เขียนแล้วอ่านกลับได้_และถูกล้างตอนสร้างบรรทัดใหม่()
    {
        var ok = "[Σ] หมายเหตุเดิม\n" + OcrSourceLineNote.Stamped(3, "PO-20261007-0001");
        Assert.Equal((3, (string?)null), OcrSourceLineNote.Parse(ok));
        var warn = OcrSourceLineNote.Unmatched("PO-20261007-0001");
        var parsed = OcrSourceLineNote.Parse(warn);
        Assert.Equal(0, parsed.Stamped);
        Assert.Contains("PO-20261007-0001", parsed.Warning);
        Assert.Contains("ไม่ทราบสัดส่วน", parsed.Warning);
        Assert.Equal((0, (string?)null), OcrSourceLineNote.Parse(null));
        // ตัวสร้างบรรทัดล้างแท็กของรอบก่อน (ไม่สะสมซ้ำ) · บรรทัดอื่นคงเดิม
        Assert.Equal("[Σ] หมายเหตุเดิม", OcrLineBuildNotes.StripRecomputed(ok));
    }

    [Fact]
    public void แก้ใบลูกผ่านทางเข้าที่ไม่ส่ง_sourceLineId_ยกค่าจากบรรทัดเดิม()
    {
        Guid s1 = Guid.NewGuid(), s2 = Guid.NewGuid();
        var prev = new List<DocumentLine> { L(1, "A", 2m, src: s1), L(2, "B", 1m, src: s2) };
        var fresh = new List<DocumentLine> { L(1, "A", 2m), L(2, "B", 5m) };
        Assert.Equal(2, SourceLineBackfill.CarryFromPrevious(prev, fresh));
        Assert.Equal(s1, fresh[0].SourceLineId);
        Assert.Equal(s2, fresh[1].SourceLineId);
    }

    [Fact]
    public void ทิศตรงข้าม_ฟอร์มส่ง_sourceLineId_กลับมาเอง_ไม่ยกทับ()
    {
        Guid s1 = Guid.NewGuid(), user = Guid.NewGuid();
        var prev = new List<DocumentLine> { L(1, "A", 2m, src: s1), L(2, "B", 1m, src: s1) };
        var fresh = new List<DocumentLine> { L(1, "A", 2m, src: user), L(2, "B", 1m) };
        Assert.Equal(0, SourceLineBackfill.CarryFromPrevious(prev, fresh));
        Assert.Equal(user, fresh[0].SourceLineId);
        Assert.Null(fresh[1].SourceLineId);
    }

    [Fact]
    public void ตัว_normalize_คำอธิบาย_ตัวเดียวกับ_SuggestLineMap()
    {
        Assert.Equal("ค่าติดตั้ง งาน", DocumentLinkPolicy.NormalizeDescription("  ค่าติดตั้ง   งาน "));
        Assert.Equal("abc", DocumentLinkPolicy.NormalizeDescription("ABC"));
        Assert.Equal("", DocumentLinkPolicy.NormalizeDescription(null));
    }
}
