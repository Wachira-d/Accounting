using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **กติกา "บรรทัดเอกสารติดลบได้ไหม" + ตัวเฉลี่ยยอดหักระดับบิล** (`Helpers/DocumentLineKind`)
///
/// ═══ ที่มา (รอบ 184 · P0) ═══
/// ระบบเดียวมีกติกาสองชุด: <c>DocumentService.ValidateDocumentLinesAsync</c> ห้ามบรรทัดติดลบ
/// (ทำให้ออเดอร์ CMS ที่มีส่วนลดสร้างเอกสารไม่ได้เลย — จ่ายเงินแล้วแต่ไม่มีอะไรในบัญชี)
/// ส่วน <c>PosTaxInvoiceLines</c> สร้างบรรทัดติดลบแล้วประกอบ <c>Document</c> เอง = เลี่ยงด่าน
///
/// เทสต์สองครึ่งตามกฎเหล็ก #4 H:
///   • ครึ่งที่พิสูจน์ว่า "ยอดหักระดับบิล" ถูกเฉลี่ยลงบรรทัดได้ครบเป๊ะ ไม่มีบรรทัดไหนติดลบ
///   • ครึ่งที่พิสูจน์ว่าบิลที่ **ไม่มียอดหัก** ไม่ถูกแตะแม้แต่สตางค์เดียว
/// </summary>
public class DocumentLineKindTests
{
    // ─────────── ครึ่งที่ 1: ยอดหักต้องเฉลี่ยได้ครบและไม่ทำให้บรรทัดติดลบ ───────────

    [Fact]
    public void ผลรวมที่เฉลี่ยต้องเท่ายอดหักเป๊ะ_แม้สัดส่วนจะปัดไม่ลงตัว()
    {
        // 3 บรรทัดที่หาร 100 ไม่ลงตัว — ปัดรายบรรทัดอิสระจะได้ 99.99 หรือ 100.01
        var alloc = DocumentLineKind.AllocateDeduction(new[] { 333.33m, 111.11m, 555.56m }, 100m);
        Assert.Equal(100m, alloc.Sum());
        Assert.All(alloc, a => Assert.True(a >= 0m));
    }

    [Fact]
    public void เศษปัดต้องไปอยู่บรรทัดที่ยอดใหญ่ที่สุด_ไม่ใช่บรรทัดสุดท้าย()
    {
        // 1 บาทบน 12 บาท: 0.83 + 0.08 + 0.08 = 0.99 ⇒ มีเศษ 0.01 ที่ต้องมีที่ลง
        var gross = new[] { 10m, 1m, 1m };
        var alloc = DocumentLineKind.AllocateDeduction(gross, 1m);
        var plain = gross.Select(g => Math.Round(1m * g / 12m, 2, MidpointRounding.AwayFromZero)).ToArray();
        var residual = 1m - plain.Sum();
        Assert.NotEqual(0m, residual);                       // เคสนี้ต้องมีเศษจริง มิฉะนั้นเทสต์ไม่ได้พิสูจน์อะไร
        Assert.Equal(plain[0] + residual, alloc[0]);         // บรรทัด 10 บาทคือบรรทัดใหญ่สุด
        Assert.Equal(plain[2], alloc[2]);                    // บรรทัดสุดท้ายไม่ใช่ที่ทิ้งเศษ
        Assert.Equal(1m, alloc.Sum());
    }

    [Fact]
    public void ยอดหักที่ไม่ได้ปัดเป็นสองตำแหน่ง_ต้องถูกหักครบเป๊ะ_ห้ามปัดทิ้ง()
    {
        // POS คิดส่วนลด% จากยอดที่มีเศษ ⇒ DiscountAmount มีทศนิยม 5 ตำแหน่งได้จริง
        // ปัดเป็น 2 ตำแหน่งที่นี่ = ยอดเอกสารคลาดจากเงินที่ลูกค้าจ่าย ต่ำกว่าเกณฑ์ที่
        // ด่าน "ยอดไม่ลงตัว" จับได้ แล้วไปโผล่เป็นลูกหนี้ค้างเศษแทน
        var alloc = DocumentLineKind.AllocateDeduction(new[] { 333.33m, 111.11m, 555.55m }, 108.32925m);
        Assert.Equal(108.32925m, alloc.Sum());
    }

    [Fact]
    public void ยอดหักเกินยอดทั้งบิล_ต้องถูกจำกัดที่ยอดบิล_ไม่มีบรรทัดไหนติดลบ()
    {
        var gross = new[] { 100m, 50m };
        var alloc = DocumentLineKind.AllocateDeduction(gross, 500m);
        Assert.Equal(150m, alloc.Sum());                      // หักได้มากสุด = ยอดทั้งบิล
        for (var i = 0; i < gross.Length; i++)
            Assert.True(gross[i] - alloc[i] >= 0m, $"บรรทัด {i} ติดลบ");
    }

    [Fact]
    public void บรรทัดจิ๋วต้องไม่ถูกหักเกินยอดของตัวเอง_และยอดรวมยังครบ()
    {
        // บรรทัด 0.01 รับส่วนลดได้มากสุด 0.01 — ที่เหลือต้องไปอยู่บรรทัด 999.99
        // (ลูปเกลี่ย "ส่วนล้น" ในตัวเฉลี่ยเป็นตาข่ายกันพลาดแบบเดียวกับ
        //  DocumentService.AllocateBillDiscount · หาอินพุต 2 ตำแหน่งที่ทำให้มันทำงานจริง
        //  ไม่เจอในการสุ่ม 400,000 เคส — เทสต์นี้จึงล็อก **ผลลัพธ์** ที่ต้องจริงเสมอ
        //  ไม่ใช่ล็อกว่าลูปนั้นถูกเรียก)
        var gross = new[] { 0.01m, 999.99m };
        var alloc = DocumentLineKind.AllocateDeduction(gross, 900m);
        Assert.Equal(900m, alloc.Sum());
        for (var i = 0; i < gross.Length; i++) Assert.True(alloc[i] <= gross[i]);
    }

    [Fact]
    public void บรรทัดที่ไม่มีฐาน_ต้องไม่ถูกหัก()
    {
        // ฐาน 0 = บรรทัด "ปัดเศษ" ที่ผู้เรียกไม่อยากให้โดนส่วนลด
        var alloc = DocumentLineKind.AllocateDeduction(new[] { 1000m, 0m }, 100m);
        Assert.Equal(0m, alloc[1]);
        Assert.Equal(100m, alloc[0]);
    }

    [Fact]
    public void บรรทัดติดลบต้องไม่ผ่านด่าน_และข้อความต้องบอกทางไปต่อ()
    {
        Assert.False(DocumentLineKind.NegativeLineAllowed);

        var v = DocumentLineKind.Judge(quantity: 1m, unitPrice: -100m, discountAmount: 0m);
        Assert.False(v.Ok);
        Assert.Contains("ใบลดหนี้", v.Reason!);               // ทางไปต่อของผู้ใช้ (F2 ข้อ 8)
        Assert.Contains("ส่วนลด", v.Reason!);

        Assert.False(DocumentLineKind.Judge(0m, 100m, 0m).Ok);       // จำนวน 0
        Assert.False(DocumentLineKind.Judge(-1m, 100m, 0m).Ok);      // จำนวนติดลบ
        Assert.False(DocumentLineKind.Judge(1m, 100m, -5m).Ok);      // ส่วนลดติดลบ
    }

    // ─────────── ครึ่งที่ 2: ของที่ถูกอยู่แล้ว ห้ามขยับ ───────────

    [Fact]
    public void ไม่มียอดหัก_ต้องไม่มีบรรทัดไหนถูกแตะ()
    {
        var alloc = DocumentLineKind.AllocateDeduction(new[] { 107m, 214m, 321m }, 0m);
        Assert.All(alloc, a => Assert.Equal(0m, a));
        Assert.Equal(0m, DocumentLineKind.AllocateDeduction(new[] { 107m }, -50m).Sum());
        Assert.Empty(DocumentLineKind.AllocateDeduction(Array.Empty<decimal>(), 100m));
    }

    [Fact]
    public void บรรทัดปกติต้องผ่านด่านเสมอ_ด่านที่ฟ้องใบถูกทุกใบคือด่านที่ปิดตัวเอง()
    {
        Assert.True(DocumentLineKind.Judge(1m, 100m, 0m).Ok);
        Assert.True(DocumentLineKind.Judge(0.5m, 0m, 0m).Ok);        // ของแถมราคา 0 ต้องออกใบได้
        Assert.True(DocumentLineKind.Judge(3m, 99.9999m, 12.34m).Ok);
        Assert.Null(DocumentLineKind.Judge(1m, 1m, 0m).Reason);
    }

    [Fact]
    public void ยอดหักที่หารลงตัวต้องได้สัดส่วนตรง_ไม่มีเศษมาเกาะบรรทัดใหญ่()
    {
        var alloc = DocumentLineKind.AllocateDeduction(new[] { 600m, 400m }, 100m);
        Assert.Equal(60m, alloc[0]);
        Assert.Equal(40m, alloc[1]);
    }
}
