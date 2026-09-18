using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **"ใบนี้เข้าข่ายหัก ณ ที่จ่ายไหม"** — ด่านที่เคยตอบว่า "ใช่" กับใบซื้อของทุกใบ
///
/// ═══ เคสจริง (ผู้ใช้รายงาน 2026-09-18) ═══
/// สแกนใบซื้อของจากร้านค้าปลีก (แผ่นปะเต็นท์ · ผ้าปูพื้น · ค่าส่ง) ยอด 5,682.24
/// แล้วระบบเตือนว่า "ต้องหัก ณ ที่จ่าย" ทั้งที่<b>การซื้อสินค้าไม่อยู่ในข่าย</b>
/// ตาม ท.ป.4/2528 — กฎเดิมไม่มีเงื่อนไขไหนถามเรื่องสินค้า/บริการเลย
///
/// ล็อกสองครึ่งตามกฎเหล็ก #4 H: ครึ่งที่ทำให้ใบซื้อของเงียบ และครึ่งที่พิสูจน์ว่า
/// <b>ใบค่าบริการยังต้องเตือนเหมือนเดิม</b> (ด่านที่เงียบทุกใบ = ไม่มีด่าน)
/// </summary>
public class WhtApplicabilityEvidenceTests
{
    private static WhtLineFact Goods(string desc, ProductType kind = ProductType.Product)
        => new(desc, null, kind, 100m);

    // ══════════ ครึ่งแรก: ใบที่ไม่ควรถูกเตือน ══════════

    [Theory]
    [InlineData(ProductType.Product)]
    [InlineData(ProductType.Supplies)]
    [InlineData(ProductType.RawMaterial)]
    [InlineData(ProductType.NonStock)]
    public void ทุกบรรทัดเป็นของ_ต้องสรุปว่าไม่เข้าข่ายหัก(ProductType kind)
        => Assert.Equal(WhtApplicability.GoodsNoWithholding,
            WhtApplicabilityEvidence.Judge(new[]
            {
                Goods("แผ่นปะซ่อมเต็นท์", kind),
                Goods("ผ้าปูพื้นชีต", kind),
            }).Level);

    // ══════════ ครึ่งหลัง: ใบที่ยังต้องเตือน ══════════

    [Fact]
    public void ระบุประเภทเงินได้ไว้แล้ว_คือการประกาศตรงๆว่าเข้าข่าย()
        => Assert.Equal(WhtApplicability.ServiceWithholding,
            WhtApplicabilityEvidence.Judge(new[]
            {
                new WhtLineFact("ค่าที่ปรึกษา", "40(2)", null, 10_000m),
            }).Level);

    [Fact]
    public void มีบรรทัดที่เป็นบริการ_ต้องเตือน()
        => Assert.Equal(WhtApplicability.ServiceWithholding,
            WhtApplicabilityEvidence.Judge(new[]
            {
                new WhtLineFact("ติดตั้งระบบ", null, ProductType.Service, 5_000m),
            }).Level);

    [Fact]
    public void ใบผสมของกับบริการ_ต้องเตือน_ไม่ใช่เงียบ()
        // §54 ให้ผู้จ่ายรับผิดในภาษีที่ไม่ได้หัก ⇒ เมื่อก้ำกึ่งให้เอนไปทางเตือน
        => Assert.Equal(WhtApplicability.ServiceWithholding,
            WhtApplicabilityEvidence.Judge(new[]
            {
                Goods("อะไหล่"),
                new WhtLineFact("ค่าแรงติดตั้ง", null, ProductType.Service, 800m),
            }).Level);

    // ══════════ "ไม่รู้" ต้องเป็น "ไม่รู้" ไม่ใช่เดาไปทางใดทางหนึ่ง ══════════

    [Fact]
    public void บรรทัดอิสระจาก_OCR_ที่ไม่ผูกรายการ_ต้องตอบว่ายังไม่รู้()
        // เคสดีแคทลอนอยู่กลุ่มนี้ — ส่งต่อให้ชั้นที่เห็นบริบทครบกว่าตัดสิน
        => Assert.Equal(WhtApplicability.Unknown,
            WhtApplicabilityEvidence.Judge(new[]
            {
                new WhtLineFact("แผ่นปะซ่อมเต็นท์ ถุงนอน", null, null, 280m),
                new WhtLineFact("Shipping costs", null, null, 0m),
            }).Level);

    [Fact]
    public void คำว่าค่าขนส่งบนใบซื้อของ_ห้ามสรุปเองว่าเป็นค่าบริการ()
        // ค่าส่งที่ผู้ขายเรียกเก็บ = ส่วนหนึ่งของราคาสินค้า ไม่ใช่สัญญาจ้างขนส่ง
        // ⇒ ห้ามให้คำในคำอธิบายเป็นข้อสรุปเดี่ยว ๆ (หลักการ "ทางลัด ≠ เงื่อนไขเดียว")
        => Assert.Equal(WhtApplicability.Unknown,
            WhtApplicabilityEvidence.Judge(new[]
            {
                new WhtLineFact("ค่าขนส่ง", null, null, 100m),
            }).Level);

    [Fact]
    public void บางบรรทัดผูกของ_บางบรรทัดไม่ผูก_ยังสรุปว่าเป็นของไม่ได้()
        => Assert.Equal(WhtApplicability.Unknown,
            WhtApplicabilityEvidence.Judge(new[]
            {
                Goods("สินค้า A"),
                new WhtLineFact("รายการอิสระ", null, null, 500m),
            }).Level);

    [Fact]
    public void ใบไม่มีบรรทัด_ต้องไม่ตัดสินแทน()
        => Assert.Equal(WhtApplicability.Unknown,
            WhtApplicabilityEvidence.Judge(System.Array.Empty<WhtLineFact>()).Level);

    [Fact]
    public void เหตุผลต้องเป็นข้อความที่เอาไปโชว์ผู้ใช้ได้()
    {
        var r = WhtApplicabilityEvidence.Judge(new[] { Goods("ของ") });
        Assert.False(string.IsNullOrWhiteSpace(r.Reason));
        Assert.Contains("ซื้อสินค้า", r.Reason);
    }
}
