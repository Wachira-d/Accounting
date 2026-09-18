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
    public void ทุกบรรทัดเป็นของ_ต้องสรุปว่าไม่เข้าข่ายหัก(ProductType kind)
        => Assert.Equal(WhtApplicability.NotApplicable,
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

    // ══════════ ชั้นที่ 0: กระดาษพูดก่อน (คำตัดสินเจ้าของ 2026-09-18) ══════════

    [Fact]
    public void ใบกำกับที่สมบูรณ์ไม่มีส่วนหักณที่จ่าย_ต้องเงียบ_แม้บรรทัดจะดูเป็นบริการ()
        // "ถ้าเป็นใบกำกับภาษีที่สมบูรณ์ กระดาษชนะ" (คำตัดสินเจ้าของ รอบ 178)
        // ⇒ ชั้นนี้ชนะแม้บรรทัดจะผูกรายการชนิดบริการ
        => Assert.Equal(WhtApplicability.NotApplicable,
            WhtApplicabilityEvidence.Judge(
                new[] { new WhtLineFact("ค่าติดตั้ง", null, ProductType.Service, 9_000m) },
                paperShowsWithholding: false,
                paperGrade: PaperTaxInvoiceGrade.Complete).Level);

    // ── ทิศตรงข้ามของชั้นที่ 0: ใบที่ไม่สมบูรณ์ "เงียบ" ไม่ได้ ──────────────
    // เดิมความเงียบของกระดาษ**ทุกใบ**ถูกนับเป็นหลักฐาน ⇒ ใบที่ OCR อ่านไม่ครบ
    // หรือใบที่ผู้ขายกรอกไม่ครบ ก็ปิดคำเตือนได้ทั้งที่ไม่มีน้ำหนักพอ
    // และที่หนักกว่านั้นคือมันข้าม **คำประกาศของมนุษย์** (ประเภทเงินได้ ม.40
    // ที่ผู้ใช้ตั้งเอง) ซึ่งเป็นหลักฐานที่แข็งกว่า "ไม่พบคำบนกระดาษ" (ทีม T1 V4)

    [Fact]
    public void ใบไม่สมบูรณ์ที่ไม่มีส่วนหัก_ต้องไม่ปิดคำประกาศของผู้ใช้()
        => Assert.Equal(WhtApplicability.ServiceWithholding,
            WhtApplicabilityEvidence.Judge(
                new[] { new WhtLineFact("ค่าที่ปรึกษา", "40(2)", null, 10_000m) },
                paperShowsWithholding: false,
                paperGrade: PaperTaxInvoiceGrade.Incomplete).Level);

    [Fact]
    public void ยังไม่ได้ตรวจความสมบูรณ์_ก็ยังไม่นับความเงียบของกระดาษ()
        // ค่าตั้งต้นของพารามิเตอร์คือ Unknown ⇒ ผู้เรียกที่ยังไม่ส่งเกรดมา
        // จะ**ไม่**ได้ผลว่า "ไม่ต้องหัก" ฟรี ๆ (เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล
        // ห้ามตกเป็น "ผ่าน" — หลักการ G3)
        => Assert.Equal(WhtApplicability.ServiceWithholding,
            WhtApplicabilityEvidence.Judge(
                new[] { new WhtLineFact("ค่าที่ปรึกษา", "40(2)", null, 10_000m) },
                paperShowsWithholding: false).Level);

    [Fact]
    public void ใบไม่สมบูรณ์ที่เป็นสินค้าล้วน_ยังเงียบได้จากชั้นบรรทัด()
        // ทิศตรงข้ามอีกด้าน: การเข้มขึ้นที่ชั้น 0 ต้อง**ไม่**ทำให้ใบซื้อของ
        // กลับมาเด้ง — ชั้นที่ 3 (ทุกบรรทัดผูกสินค้า) ยังรับไว้เหมือนเดิม
        => Assert.Equal(WhtApplicability.NotApplicable,
            WhtApplicabilityEvidence.Judge(
                new[] { Goods("แผ่นปะเต็นท์"), Goods("ผ้าปูพื้น") },
                paperShowsWithholding: false,
                paperGrade: PaperTaxInvoiceGrade.Incomplete).Level);

    [Fact]
    public void กระดาษมีส่วนหักณที่จ่าย_ต้องเตือน_แม้บรรทัดจะเป็นสินค้า()
        // ทิศตรงข้าม: ผู้ขายพิมพ์บรรทัดหักมาเอง = หลักฐานตรงว่าอยู่ในข่าย
        => Assert.Equal(WhtApplicability.ServiceWithholding,
            WhtApplicabilityEvidence.Judge(
                new[] { Goods("อะไหล่") }, paperShowsWithholding: true,
                paperGrade: PaperTaxInvoiceGrade.Incomplete).Level);   // ใบไม่ครบก็ยังชนะ — เป็นคำประกาศเชิงบวก

    [Fact]
    public void ไม่มีกระดาษให้ดู_ต้องไม่ถือว่ากระดาษบอกว่าไม่ต้องหัก()
        // null = คีย์มือ/สร้างจากใบอื่น ⇒ ต้องไหลไปชั้นถัดไป ไม่ใช่เงียบ
        => Assert.Equal(WhtApplicability.ServiceWithholding,
            WhtApplicabilityEvidence.Judge(
                new[] { new WhtLineFact("ค่าที่ปรึกษา", "40(2)", null, 10_000m) },
                paperShowsWithholding: null).Level);

    [Fact]
    public void รายการชนิด_NonStock_ไม่ใช่หลักฐานว่าเป็นของ()
        // หน้าจัดการสินค้าติดป้ายชนิดนี้ว่า "อื่นๆ" — เป็นถังรวม ไม่ใช่หลักฐาน
        // ⇒ ต้องตกไปให้ชั้นเรียนรู้ตัดสิน ไม่ใช่เงียบเอง (ฝ่ายค้านรอบ 177)
        => Assert.Equal(WhtApplicability.Unknown,
            WhtApplicabilityEvidence.Judge(new[] { Goods("รายการอื่นๆ", ProductType.NonStock) }).Level);
}
