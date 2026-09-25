using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 194 ทีม B — กติกาของเส้นเอกสารที่ใช้ประเภทเงินมัดจำ (<see cref="DepositKindDocumentRules"/>) · ล็อกสองครึ่ง:
/// <b>ครึ่งแรก</b> ของเดิม/ใบเดิมไม่ถูกแตะ (ไม่ส่งประเภท = คงเดิม · ใบเดิม NULL ไม่เตือน · VAT เสียแล้วไม่ถาม · บริษัทไม่จด VAT ไม่ถาม) ·
/// <b>ครึ่งหลัง</b> ใบใหม่/ใบที่ต้องถามได้คำตอบถูก (ล้าง/เปลี่ยนประเภท · ล็อกใบที่อนุมัติแล้ว · ถามเงินที่ริบ · กลับ VAT พักตามสัดส่วน · เตือนตอนอนุมัติ)
/// ตัวเลข VAT ทุกตัวมาจาก <see cref="DepositPolicyResolver.ForfeitVatDecision"/> / <see cref="DepositPolicyResolver.KindWarning"/> ตัวเดียว
/// </summary>
public class DepositKindDocumentRulesTests
{
    private static readonly Guid KindA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid KindB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // ═════════════════ ครึ่งแรก — ของเดิมไม่ถูกแตะ ═════════════════

    [Fact]
    public void แก้ใบโดยไม่ส่งประเภท_คงประเภทเดิม_ไม่นับว่าเปลี่ยน()
    {
        Assert.Equal(((Guid?)KindA, false), DepositKindDocumentRules.UpdateTarget(null, KindA));
        Assert.Equal(((Guid?)null, false), DepositKindDocumentRules.UpdateTarget(null, null));
        // ส่ง id เดิมซ้ำ (ฟอร์มส่งทุกครั้ง) = ไม่ใช่การเปลี่ยน ⇒ ใบที่อนุมัติแล้วแก้หัวใบได้ตามเดิม ไม่ถูกด่านเปลี่ยนประเภทกัน
        Assert.Equal(((Guid?)KindA, false), DepositKindDocumentRules.UpdateTarget(KindA, KindA));
    }

    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.Rejected)]
    public void ใบร่างและใบถูกตีกลับ_เปลี่ยนประเภทได้(DocumentStatus st)
        => Assert.Null(DepositKindDocumentRules.KindChangeBlockedReason(st));

    [Fact]
    public void ใบเดิมไม่ทราบลักษณะ_ไม่เตือนตอนอนุมัติ_พฤติกรรมเดิม()
    {
        // ใบเดิม (NULL) มัดจำเต็มยอด VAT 0 + deferred — เดิมไม่มีคำเตือน ต้องยังไม่มี
        Assert.Null(DepositKindDocumentRules.ApprovalWarning(true, null, 0m, true, true, DepositSupplyNature.Service, null));
        // ไม่ใช่ใบมัดจำ = ไม่เกี่ยว
        Assert.Null(DepositKindDocumentRules.ApprovalWarning(false, DepositNature.PartOfPrice, 0m, true, true, DepositSupplyNature.Service, null));
    }

    [Fact]
    public void ราคาVATทันที_และเงินประกันเต็มยอด_ไม่เตือน_ใบที่ถูกต้องห้ามถูกฟ้อง()
    {
        Assert.Null(DepositKindDocumentRules.ApprovalWarning(true, DepositNature.PartOfPrice, 65.42m, false, true, DepositSupplyNature.Service, null));
        Assert.Null(DepositKindDocumentRules.ApprovalWarning(true, DepositNature.RefundableSecurity, 0m, true, true, DepositSupplyNature.Service, null));
        Assert.Null(DepositKindDocumentRules.ApprovalWarning(true, DepositNature.NonVatSupply, 0m, true, true, DepositSupplyNature.Service, null));
    }

    [Fact]
    public void บริษัทไม่จดVAT_ใบVAT0_ไม่ถูกเตือนว่าเลื่อนVAT()
        => Assert.Null(DepositKindDocumentRules.ApprovalWarning(true, DepositNature.PartOfPrice, 0m, false, false, DepositSupplyNature.Goods, null));

    [Fact]
    public void ใบที่VATเสียไปแล้ว_ไม่ถามว่าเงินที่ริบคืออะไร()
    {
        // ใบเดิม VAT ทันที 65.42 — ริบแล้ว VAT คงเดิม ไม่ว่าตอบอะไร ⇒ ถาม = ภาระเปล่า
        Assert.Null(DepositKindDocumentRules.ForfeitOptions(null, 65.42m, false, 7m));
        // ราคา: ตอบอะไรก็เป็นราคา · นอกระบบ VAT: ไม่มี VAT เสมอ
        Assert.Null(DepositKindDocumentRules.ForfeitOptions(DepositNature.PartOfPrice, 0m, false, 7m));
        Assert.Null(DepositKindDocumentRules.ForfeitOptions(DepositNature.NonVatSupply, 0m, false, 7m));
    }

    [Fact]
    public void บริษัทไม่จดVAT_ไม่ถาม_ป้ายมีVATจะเป็นข้อความเท็จ()
        => Assert.Null(DepositKindDocumentRules.ForfeitOptions(null, 0m, false, 0m));

    [Fact]
    public void กลับVATพัก_ไม่มีอะไรพัก_หรือไม่ได้ริบ_เป็นศูนย์()
    {
        Assert.Equal(0m, DepositKindDocumentRules.CompensationVatReversal(0m, 500m, 1000m));
        Assert.Equal(0m, DepositKindDocumentRules.CompensationVatReversal(70m, 0m, 1000m));
        Assert.Equal(0m, DepositKindDocumentRules.CompensationVatReversal(70m, 500m, 0m));
    }

    // ═════════════════ ครึ่งหลัง — ใบใหม่/ใบที่ต้องถามได้คำตอบถูก ═════════════════

    [Fact]
    public void GuidEmpty_คือล้างประเภท_ค่าใหม่คือเปลี่ยน()
    {
        Assert.Equal(((Guid?)null, true), DepositKindDocumentRules.UpdateTarget(Guid.Empty, KindA));
        Assert.Equal(((Guid?)KindB, true), DepositKindDocumentRules.UpdateTarget(KindB, KindA));
        Assert.Equal(((Guid?)KindA, true), DepositKindDocumentRules.UpdateTarget(KindA, null));
        // ล้างใบที่ไม่มีประเภทอยู่แล้ว = ไม่เปลี่ยน
        Assert.Equal(((Guid?)null, false), DepositKindDocumentRules.UpdateTarget(Guid.Empty, null));
    }

    [Theory]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Paid)]
    [InlineData(DocumentStatus.Voided)]
    public void ใบที่อนุมัติแล้ว_เปลี่ยนประเภทไม่ได้_และบอกทางไปต่อ(DocumentStatus st)
    {
        var why = DepositKindDocumentRules.KindChangeBlockedReason(st);
        Assert.NotNull(why);
        Assert.Contains("§86/4", why);
        Assert.Contains("ทางไปต่อ", why);
    }

    [Fact]
    public void ใบเดิมมัดจำเต็มยอด_ถามเงินที่ริบ_ค่าเริ่มต้นมีVAT_และออกใบกำกับ()
    {
        var opts = DepositKindDocumentRules.ForfeitOptions(null, 0m, true, 7m, depositOutputVatDeferred: true);
        Assert.NotNull(opts);
        Assert.Equal(2, opts!.Count);
        var def = Assert.Single(opts, o => o.IsDefault);
        Assert.Equal(nameof(DepositForfeitAs.PriceOrFee), def.Value);
        Assert.Contains("ใบกำกับภาษี", def.Description);   // คำอธิบายมาจาก ForfeitVatDecision (IssueTaxInvoiceForForfeit)
        var comp = Assert.Single(opts, o => !o.IsDefault);
        Assert.Equal(nameof(DepositForfeitAs.Compensation), comp.Value);
        Assert.Contains("ไม่มี VAT", comp.Description);
        // ข้อความในหน้าต่างก่อนกด = ผลของค่าเริ่มต้น
        Assert.Contains("ออกใบกำกับภาษี", DepositKindDocumentRules.DefaultForfeitExplanation(null, 0m, true, 7m, depositOutputVatDeferred: true));
    }

    [Fact]
    public void เงินประกัน_และใบเดิมVATพัก_ถามเงินที่ริบ()
    {
        Assert.NotNull(DepositKindDocumentRules.ForfeitOptions(DepositNature.RefundableSecurity, 0m, true, 7m, depositOutputVatDeferred: true));
        // VAT พัก 21913: ราคา = ย้ายเข้า 21911 · ค่าเสียหาย = กลับเข้ารายได้ ⇒ ผลต่างกัน ต้องถาม
        Assert.NotNull(DepositKindDocumentRules.ForfeitOptions(null, 65.42m, true, 7m));
    }

    [Fact]
    public void กลับVATพักตามสัดส่วน_ริบครบคงเหลือกลับทั้งก้อนไม่ทิ้งเศษ()
    {
        // มัดจำ 1,000 รวม VAT พัก 65.42 (ฐาน 934.58) — ริบครึ่งฐาน = 32.71 · ริบที่เหลือทั้งหมด = ยอดที่ค้างจริงใน GL
        Assert.Equal(32.71m, DepositKindDocumentRules.CompensationVatReversal(65.42m, 467.29m, 934.58m));
        Assert.Equal(65.42m, DepositKindDocumentRules.CompensationVatReversal(65.42m, 934.58m, 934.58m));
        Assert.Equal(32.71m, DepositKindDocumentRules.CompensationVatReversal(32.71m, 467.29m, 467.29m));
        // ไม่เกินยอดที่ค้างใน GL
        Assert.True(DepositKindDocumentRules.CompensationVatReversal(10m, 900m, 934.58m) <= 10m);
    }

    [Fact]
    public void ราคาเลื่อนVAT_เตือนตอนอนุมัติพร้อมรหัสกฎและเหตุผลบนใบ_สินค้าแรงเท่าบริการ()
    {
        var goods = DepositKindDocumentRules.ApprovalWarning(true, DepositNature.PartOfPrice, 0m, true, true,
            DepositSupplyNature.Goods, "[RD-78(1)(b)] เลือกเลื่อน VAT — เหตุผล: สัญญาเลขที่ 12");
        Assert.NotNull(goods);
        Assert.StartsWith($"[{DepositPolicyResolver.KindPriceGoodsRuleCode}]", goods);
        Assert.Contains("สัญญาเลขที่ 12", goods);
        var service = DepositKindDocumentRules.ApprovalWarning(true, DepositNature.PartOfPrice, 65.42m, true, true,
            DepositSupplyNature.Service, null);
        Assert.StartsWith($"[{DepositPolicyResolver.KindPriceServiceRuleCode}]", service);
    }

    [Fact]
    public void เงินประกันแยกVAT_เตือนตอนอนุมัติ()
    {
        var w = DepositKindDocumentRules.ApprovalWarning(true, DepositNature.RefundableSecurity, 65.42m, false, true,
            DepositSupplyNature.Service, null);
        Assert.NotNull(w);
        Assert.StartsWith($"[{DepositPolicyResolver.KindSecurityEarlyVatRuleCode}]", w);
    }
}
