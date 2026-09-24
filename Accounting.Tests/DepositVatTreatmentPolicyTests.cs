using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// วิธีบันทึกเงินมัดจำ (รอบ 193 · คำตัดสินเจ้าของ #34 + คำชี้แจง "ขึ้นอยู่กับประเภทธุรกิจ · ต้องตั้งค่าได้ทั้งหมด")
/// — ล็อกสองทิศ: (1) บริการ = VAT ทันที (§78/1) และคำเตือนเมื่อเลือกอย่างอื่น (2) สินค้า/ไม่ทราบ/ข้อมูลเดิม
/// = พฤติกรรมเดิม (VAT ทันที) ไม่เปลี่ยนเงียบ ๆ · ตัวเลขใบมัดจำ 1,000 รวม VAT ตามโหมด
/// </summary>
public class DepositVatTreatmentPolicyTests
{
    // ── ลำดับชั้น: ช่องทาง → บริษัท → ประเภทธุรกิจ ──

    [Fact]
    public void บริการ_ยังไม่มีใครตั้ง_ได้VATทันที_ไม่เตือน()
    {
        var d = DepositVatTreatmentPolicy.Resolve(DepositSupplyNature.Service, null);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.Equal(DepositVatTreatmentSource.BusinessTypeDefault, d.Source);
        Assert.False(d.NeedsOwnerChoice);
        Assert.Null(d.Warning);
    }

    [Fact]
    public void สินค้า_ยังไม่มีใครตั้ง_คงพฤติกรรมเดิม_VATทันที_ไม่เตือน()
    {
        var d = DepositVatTreatmentPolicy.Resolve(DepositSupplyNature.Goods, null);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.False(d.NeedsOwnerChoice);
        Assert.Null(d.Warning);
    }

    [Fact]
    public void ไม่ทราบประเภทธุรกิจ_คงพฤติกรรมเดิมแต่ขอให้เจ้าของเลือก()
    {
        var d = DepositVatTreatmentPolicy.Resolve(DepositSupplyNature.Unknown, null);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.True(d.NeedsOwnerChoice);
        // ตั้งเองแล้ว = ไม่ต้องถามอีก
        Assert.False(DepositVatTreatmentPolicy.Resolve(DepositSupplyNature.Unknown, DepositVatTreatment.VatImmediate).NeedsOwnerChoice);
    }

    [Theory]
    [InlineData(DepositVatTreatment.FullDeposit)]
    [InlineData(DepositVatTreatment.VatPendingUndue)]
    public void บริการเลือกโหมดที่ไม่ใช่VATทันที_ได้คำเตือน78_1(DepositVatTreatment t)
    {
        var d = DepositVatTreatmentPolicy.Resolve(DepositSupplyNature.Service, t);
        Assert.Equal(t, d.Treatment);
        Assert.Equal(DepositVatTreatmentSource.CompanySetting, d.Source);
        Assert.Equal(DepositVatTreatmentPolicy.ServiceNonImmediateRuleCode, d.WarningRuleCode);
        Assert.Contains("78/1", d.Warning);
    }

    [Fact]
    public void สินค้าเลือกภาษีรอเรียกเก็บ_ได้แค่แจ้งให้ทราบ_ไม่ใช่คำเตือน78_1()
    {
        var d = DepositVatTreatmentPolicy.Resolve(DepositSupplyNature.Goods, DepositVatTreatment.VatPendingUndue);
        Assert.Equal(DepositVatTreatmentPolicy.GoodsNonImmediateRuleCode, d.WarningRuleCode);
        Assert.DoesNotContain("78/1", d.Warning);
    }

    [Fact]
    public void ที่พักตั้งทับ_ชนะค่าบริษัท_ทั้งสองทิศ()
    {
        var defer = DepositVatTreatmentPolicy.Resolve(DepositSupplyNature.Service, DepositVatTreatment.VatImmediate, DepositVatTreatment.VatPendingUndue);
        Assert.Equal(DepositVatTreatment.VatPendingUndue, defer.Treatment);
        Assert.Equal(DepositVatTreatmentSource.ChannelOverride, defer.Source);
        Assert.NotNull(defer.Warning);

        var immediate = DepositVatTreatmentPolicy.Resolve(DepositSupplyNature.Service, DepositVatTreatment.FullDeposit, DepositVatTreatment.VatImmediate);
        Assert.Equal(DepositVatTreatment.VatImmediate, immediate.Treatment);
        Assert.Null(immediate.Warning);
    }

    [Fact]
    public void ค่าที่ไม่มีในระบบ_ไม่ถูกใช้_ตกไปชั้นถัดไป()
    {
        var d = DepositVatTreatmentPolicy.Resolve(DepositSupplyNature.Service, (DepositVatTreatment)0, (DepositVatTreatment)99);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.Equal(DepositVatTreatmentSource.BusinessTypeDefault, d.Source);
        Assert.False(DepositVatTreatmentPolicy.IsDefined((DepositVatTreatment)0));
        Assert.False(DepositVatTreatmentPolicy.IsDefined(null));
    }

    [Theory]
    [InlineData(IndustryType.Hotel, DepositSupplyNature.Service)]
    [InlineData(IndustryType.Service, DepositSupplyNature.Service)]
    [InlineData(IndustryType.Construction, DepositSupplyNature.Service)]
    [InlineData(IndustryType.Retail, DepositSupplyNature.Goods)]
    [InlineData(IndustryType.Trading, DepositSupplyNature.Goods)]
    [InlineData(IndustryType.General, DepositSupplyNature.Unknown)]
    [InlineData(IndustryType.Restaurant, DepositSupplyNature.Unknown)]
    [InlineData(IndustryType.Other, DepositSupplyNature.Unknown)]
    public void ประเภทธุรกิจ_แมปลักษณะสิ่งที่ขาย(IndustryType i, DepositSupplyNature expected)
        => Assert.Equal(expected, DepositVatTreatmentPolicy.NatureOf(i));

    // ── รูปของใบมัดจำ + ยอด JE ต่อโหมด (1,000 รวม VAT 7%) ──

    [Fact]
    public void มัดจำเต็มยอด_1000_ลงหนี้สินเต็ม_ไม่มีVAT()
    {
        var p = DepositVatTreatmentPolicy.PreviewReceipt(DepositVatTreatment.FullDeposit, 1000m, 7m);
        Assert.Equal(1000m, p.Liability);
        Assert.Equal(0m, p.OutputVatDue);
        Assert.Equal(0m, p.OutputVatUndue);
        Assert.Equal(new DepositDocumentShape(0m, true), DepositVatTreatmentPolicy.ShapeFor(DepositVatTreatment.FullDeposit, 7m));
    }

    [Fact]
    public void ภาษีรอเรียกเก็บ_1000_ฐาน934_58_VATพัก65_42()
    {
        var p = DepositVatTreatmentPolicy.PreviewReceipt(DepositVatTreatment.VatPendingUndue, 1000m, 7m);
        Assert.Equal(934.58m, p.Liability);
        Assert.Equal(0m, p.OutputVatDue);
        Assert.Equal(65.42m, p.OutputVatUndue);
    }

    [Fact]
    public void VATทันที_1000_ฐาน934_58_ภาษีขาย65_42()
    {
        var p = DepositVatTreatmentPolicy.PreviewReceipt(DepositVatTreatment.VatImmediate, 1000m, 7m);
        Assert.Equal(934.58m, p.Liability);
        Assert.Equal(65.42m, p.OutputVatDue);
        Assert.Equal(0m, p.OutputVatUndue);
        Assert.Equal(p.Gross, p.Liability + p.OutputVatDue);
    }

    [Theory]
    [InlineData(DepositVatTreatment.FullDeposit)]
    [InlineData(DepositVatTreatment.VatPendingUndue)]
    [InlineData(DepositVatTreatment.VatImmediate)]
    public void บริษัทไม่คิดVAT_ทุกโหมดไม่มีVAT_เหมือนเดิม(DepositVatTreatment t)
    {
        Assert.Equal(new DepositDocumentShape(0m, false), DepositVatTreatmentPolicy.ShapeFor(t, 0m));
        Assert.Equal(1000m, DepositVatTreatmentPolicy.PreviewReceipt(t, 1000m, 0m).Liability);
    }

    // ── อ่านโหมดย้อนจากใบที่ออกแล้ว (ข้อมูลเดิมไม่ถูกเขียนย้อน) ──

    [Fact]
    public void ใบมัดจำเดิม_อ่านโหมดย้อนได้จากช่องที่ตรึงไว้()
    {
        Assert.Equal(DepositVatTreatment.VatImmediate, DepositVatTreatmentPolicy.OfDocument(true, 130.84m, false));
        Assert.Equal(DepositVatTreatment.VatPendingUndue, DepositVatTreatmentPolicy.OfDocument(true, 130.84m, true));
        Assert.Equal(DepositVatTreatment.FullDeposit, DepositVatTreatmentPolicy.OfDocument(true, 0m, true));
        Assert.Null(DepositVatTreatmentPolicy.OfDocument(false, 130.84m, false));
    }

    [Fact]
    public void คำอธิบายยอดJEบนใบมัดจำ_ตรงกับโหมด()
    {
        Assert.Contains("ภาษีขาย 65.42 (21911", DepositVatTreatmentPolicy.DescribePosting(DepositVatTreatment.VatImmediate, 1000m, 7m));
        Assert.Contains("ภาษีขายรอเรียกเก็บ 65.42 (21913)", DepositVatTreatmentPolicy.DescribePosting(DepositVatTreatment.VatPendingUndue, 1000m, 7m));
        Assert.DoesNotContain("ภาษีขาย", DepositVatTreatmentPolicy.DescribePosting(DepositVatTreatment.FullDeposit, 1000m, 7m));
    }

    // ── integration (TakeTime): ธงเมื่อขัด · ไม่ธงเมื่อสอดคล้อง ──

    [Fact]
    public void คู่ค้าพักVAT_แต่บริษัทบริการตั้งVATทันที_ติดธง78_1()
    {
        var company = DepositVatTreatmentPolicy.Resolve(DepositSupplyNature.Service, null);
        var note = DepositVatTreatmentPolicy.IntegrationMismatchNote(payloadDeferred: true, company);
        Assert.NotNull(note);
        Assert.Contains(DepositVatTreatmentPolicy.ServiceNonImmediateRuleCode, note);
        Assert.Contains("ไม่แก้ยอด", note);
    }

    [Fact]
    public void คู่ค้าสอดคล้องกับการตั้งค่า_ไม่ติดธง()
    {
        Assert.Null(DepositVatTreatmentPolicy.IntegrationMismatchNote(false, DepositVatTreatmentPolicy.Resolve(DepositSupplyNature.Service, null)));
        // เต็มยอด/รอเรียกเก็บ = "ยังไม่รับรู้" ทั้งคู่ — payload บอกได้แค่ธงเดียว ห้ามธงเท็จ
        Assert.Null(DepositVatTreatmentPolicy.IntegrationMismatchNote(true, DepositVatTreatmentPolicy.Resolve(DepositSupplyNature.Service, DepositVatTreatment.FullDeposit)));
        Assert.NotNull(DepositVatTreatmentPolicy.IntegrationMismatchNote(false, DepositVatTreatmentPolicy.Resolve(DepositSupplyNature.Goods, DepositVatTreatment.VatPendingUndue)));
    }

    [Fact]
    public void ต่อธงซ้ำ_ไม่ซ้ำข้อความ()
    {
        var once = DepositVatTreatmentPolicy.AppendNoteOnce("เดิม", "[X] ธง");
        Assert.Equal("เดิม · [X] ธง", once);
        Assert.Equal(once, DepositVatTreatmentPolicy.AppendNoteOnce(once, "[X] ธง"));
        Assert.Equal("เดิม", DepositVatTreatmentPolicy.AppendNoteOnce("เดิม", null));
    }

    // ── ป้ายบนใบสุดท้าย (สอง renderer ใช้ตัวนี้ตัวเดียว) ──

    [Fact]
    public void แถวหักท้ายบิล_เป็นมัดจำที่ออกใบกำกับแล้ว_เฉพาะรูปของเช็คเอาต์()
    {
        Assert.True(DepositVatTreatmentPolicy.BillDeductionIsTaxedDeposit(1869.16m, 0m, "TIV-2026-0001"));
        // ส่วนลดการค้าธรรมดา — ไม่มีเลขใบมัดจำ
        Assert.False(DepositVatTreatmentPolicy.BillDeductionIsTaxedDeposit(100m, 0m, null));
        // เส้น "หักเงินมัดจำจากยอดชำระ" (ApplyDeposit) — เป็นแถวคนละแถว
        Assert.False(DepositVatTreatmentPolicy.BillDeductionIsTaxedDeposit(100m, 2000m, "REC-0001"));
        Assert.False(DepositVatTreatmentPolicy.BillDeductionIsTaxedDeposit(0m, 0m, "TIV-2026-0001"));
    }

    [Fact]
    public void ตัวเลือกเรียงตามเจ้าของ_และคำเตือนบริการมีเฉพาะโหมดที่ไม่ใช่VATทันที()
    {
        var o = DepositVatTreatmentPolicy.Options;
        Assert.Equal(new[] { "FullDeposit", "VatPendingUndue", "VatImmediate" }, o.Select(x => x.Value).ToArray());
        Assert.NotNull(o[0].ServiceWarning);
        Assert.NotNull(o[1].ServiceWarning);
        Assert.Null(o[2].ServiceWarning);
        Assert.All(o, x => Assert.False(string.IsNullOrWhiteSpace(x.LegalReference)));
    }
}
