using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// วิธีบันทึกเงินมัดจำ (รอบ 193 · คำตัดสินเจ้าของ #34 + คำชี้แจง "ขึ้นอยู่กับประเภทธุรกิจ · ต้องตั้งค่าได้ทั้งหมด")
/// — ล็อกสองทิศ: (1) บริการ = VAT ทันที (§78/1) และคำเตือนเมื่อเลือกอย่างอื่น (2) สินค้า/ไม่ทราบ/ข้อมูลเดิม
/// = พฤติกรรมเดิม (VAT ทันที) ไม่เปลี่ยนเงียบ ๆ · ตัวเลขใบมัดจำ 1,000 รวม VAT ตามโหมด
/// </summary>
public class DepositPolicyResolverTests
{
    // ── ลำดับชั้น: ช่องทาง → บริษัท → ประเภทธุรกิจ ──

    [Fact]
    public void บริการ_ยังไม่มีใครตั้ง_ได้VATทันที_ไม่เตือน()
    {
        var d = DepositPolicyResolver.Resolve(DepositSupplyNature.Service, null);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.Equal(DepositVatTreatmentSource.BusinessTypeDefault, d.Source);
        Assert.False(d.NeedsOwnerChoice);
        Assert.Null(d.Warning);
    }

    [Fact]
    public void สินค้า_ยังไม่มีใครตั้ง_คงพฤติกรรมเดิม_VATทันที_ไม่เตือน()
    {
        var d = DepositPolicyResolver.Resolve(DepositSupplyNature.Goods, null);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.False(d.NeedsOwnerChoice);
        Assert.Null(d.Warning);
    }

    [Fact]
    public void ไม่ทราบประเภทธุรกิจ_คงพฤติกรรมเดิมแต่ขอให้เจ้าของเลือก()
    {
        var d = DepositPolicyResolver.Resolve(DepositSupplyNature.Unknown, null);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.True(d.NeedsOwnerChoice);
        // ตั้งเองแล้ว = ไม่ต้องถามอีก
        Assert.False(DepositPolicyResolver.Resolve(DepositSupplyNature.Unknown, DepositVatTreatment.VatImmediate).NeedsOwnerChoice);
    }

    [Theory]
    [InlineData(DepositVatTreatment.FullDeposit)]
    [InlineData(DepositVatTreatment.VatPendingUndue)]
    public void บริการเลือกโหมดที่ไม่ใช่VATทันที_ได้คำเตือน78_1(DepositVatTreatment t)
    {
        var d = DepositPolicyResolver.Resolve(DepositSupplyNature.Service, t);
        Assert.Equal(t, d.Treatment);
        Assert.Equal(DepositVatTreatmentSource.CompanySetting, d.Source);
        Assert.Equal(DepositPolicyResolver.ServiceNonImmediateRuleCode, d.WarningRuleCode);
        Assert.Contains("78/1", d.Warning);
    }

    [Fact]
    public void สินค้าเลือกภาษีรอเรียกเก็บ_ได้แค่แจ้งให้ทราบ_ไม่ใช่คำเตือน78_1()
    {
        var d = DepositPolicyResolver.Resolve(DepositSupplyNature.Goods, DepositVatTreatment.VatPendingUndue);
        Assert.Equal(DepositPolicyResolver.GoodsNonImmediateRuleCode, d.WarningRuleCode);
        Assert.DoesNotContain("78/1", d.Warning);
    }

    [Fact]
    public void ที่พักตั้งทับ_ชนะค่าบริษัท_ทั้งสองทิศ()
    {
        var defer = DepositPolicyResolver.Resolve(DepositSupplyNature.Service, DepositVatTreatment.VatImmediate, DepositVatTreatment.VatPendingUndue);
        Assert.Equal(DepositVatTreatment.VatPendingUndue, defer.Treatment);
        Assert.Equal(DepositVatTreatmentSource.ChannelOverride, defer.Source);
        Assert.NotNull(defer.Warning);

        var immediate = DepositPolicyResolver.Resolve(DepositSupplyNature.Service, DepositVatTreatment.FullDeposit, DepositVatTreatment.VatImmediate);
        Assert.Equal(DepositVatTreatment.VatImmediate, immediate.Treatment);
        Assert.Null(immediate.Warning);
    }

    [Fact]
    public void ค่าที่ไม่มีในระบบ_ไม่ถูกใช้_ตกไปชั้นถัดไป()
    {
        var d = DepositPolicyResolver.Resolve(DepositSupplyNature.Service, (DepositVatTreatment)0, (DepositVatTreatment)99);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.Equal(DepositVatTreatmentSource.BusinessTypeDefault, d.Source);
        Assert.False(DepositPolicyResolver.IsDefined((DepositVatTreatment)0));
        Assert.False(DepositPolicyResolver.IsDefined(null));
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
        => Assert.Equal(expected, DepositPolicyResolver.NatureOf(i));

    // ── รูปของใบมัดจำ + ยอด JE ต่อโหมด (1,000 รวม VAT 7%) ──

    [Fact]
    public void มัดจำเต็มยอด_1000_ลงหนี้สินเต็ม_ไม่มีVAT()
    {
        var p = DepositPolicyResolver.PreviewReceipt(DepositVatTreatment.FullDeposit, 1000m, 7m);
        Assert.Equal(1000m, p.Liability);
        Assert.Equal(0m, p.OutputVatDue);
        Assert.Equal(0m, p.OutputVatUndue);
        Assert.Equal(new DepositDocumentShape(0m, true), DepositPolicyResolver.ShapeFor(DepositVatTreatment.FullDeposit, 7m));
    }

    [Fact]
    public void ภาษีรอเรียกเก็บ_1000_ฐาน934_58_VATพัก65_42()
    {
        var p = DepositPolicyResolver.PreviewReceipt(DepositVatTreatment.VatPendingUndue, 1000m, 7m);
        Assert.Equal(934.58m, p.Liability);
        Assert.Equal(0m, p.OutputVatDue);
        Assert.Equal(65.42m, p.OutputVatUndue);
    }

    [Fact]
    public void VATทันที_1000_ฐาน934_58_ภาษีขาย65_42()
    {
        var p = DepositPolicyResolver.PreviewReceipt(DepositVatTreatment.VatImmediate, 1000m, 7m);
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
        Assert.Equal(new DepositDocumentShape(0m, false), DepositPolicyResolver.ShapeFor(t, 0m));
        Assert.Equal(1000m, DepositPolicyResolver.PreviewReceipt(t, 1000m, 0m).Liability);
    }

    // ── อ่านโหมดย้อนจากใบที่ออกแล้ว (ข้อมูลเดิมไม่ถูกเขียนย้อน) ──

    [Fact]
    public void ใบมัดจำเดิม_อ่านโหมดย้อนได้จากช่องที่ตรึงไว้()
    {
        Assert.Equal(DepositVatTreatment.VatImmediate, DepositPolicyResolver.OfDocument(true, 130.84m, false));
        Assert.Equal(DepositVatTreatment.VatPendingUndue, DepositPolicyResolver.OfDocument(true, 130.84m, true));
        Assert.Equal(DepositVatTreatment.FullDeposit, DepositPolicyResolver.OfDocument(true, 0m, true));
        Assert.Null(DepositPolicyResolver.OfDocument(false, 130.84m, false));
    }

    [Fact]
    public void คำอธิบายยอดJEบนใบมัดจำ_ตรงกับโหมด()
    {
        Assert.Contains("ภาษีขาย 65.42 (21911", DepositPolicyResolver.DescribePosting(DepositVatTreatment.VatImmediate, 1000m, 7m));
        Assert.Contains("ภาษีขายรอเรียกเก็บ 65.42 (21913)", DepositPolicyResolver.DescribePosting(DepositVatTreatment.VatPendingUndue, 1000m, 7m));
        Assert.DoesNotContain("ภาษีขาย", DepositPolicyResolver.DescribePosting(DepositVatTreatment.FullDeposit, 1000m, 7m));
    }

    // ── integration (TakeTime): ธงเมื่อขัด · ไม่ธงเมื่อสอดคล้อง ──

    [Fact]
    public void คู่ค้าพักVAT_แต่บริษัทบริการตั้งVATทันที_ติดธง78_1()
    {
        var company = DepositPolicyResolver.Resolve(DepositSupplyNature.Service, null);
        var note = DepositPolicyResolver.IntegrationMismatchNote(payloadDeferred: true, company);
        Assert.NotNull(note);
        Assert.Contains(DepositPolicyResolver.ServiceNonImmediateRuleCode, note);
        Assert.Contains("ไม่แก้ยอด", note);
    }

    [Fact]
    public void คู่ค้าสอดคล้องกับการตั้งค่า_ไม่ติดธง()
    {
        Assert.Null(DepositPolicyResolver.IntegrationMismatchNote(false, DepositPolicyResolver.Resolve(DepositSupplyNature.Service, null)));
        // เต็มยอด/รอเรียกเก็บ = "ยังไม่รับรู้" ทั้งคู่ — payload บอกได้แค่ธงเดียว ห้ามธงเท็จ
        Assert.Null(DepositPolicyResolver.IntegrationMismatchNote(true, DepositPolicyResolver.Resolve(DepositSupplyNature.Service, DepositVatTreatment.FullDeposit)));
        Assert.NotNull(DepositPolicyResolver.IntegrationMismatchNote(false, DepositPolicyResolver.Resolve(DepositSupplyNature.Goods, DepositVatTreatment.VatPendingUndue)));
    }

    [Fact]
    public void ต่อธงซ้ำ_ไม่ซ้ำข้อความ()
    {
        var once = DepositPolicyResolver.AppendNoteOnce("เดิม", "[X] ธง");
        Assert.Equal("เดิม · [X] ธง", once);
        Assert.Equal(once, DepositPolicyResolver.AppendNoteOnce(once, "[X] ธง"));
        Assert.Equal("เดิม", DepositPolicyResolver.AppendNoteOnce("เดิม", null));
    }

    // ── ป้ายบนใบสุดท้าย (สอง renderer ใช้ตัวนี้ตัวเดียว) ──

    [Fact]
    public void แถวหักท้ายบิล_เป็นมัดจำที่ออกใบกำกับแล้ว_เฉพาะรูปของเช็คเอาต์()
    {
        Assert.True(DepositPolicyResolver.BillDeductionIsTaxedDeposit(1869.16m, 0m, "TIV-2026-0001"));
        // ส่วนลดการค้าธรรมดา — ไม่มีเลขใบมัดจำ
        Assert.False(DepositPolicyResolver.BillDeductionIsTaxedDeposit(100m, 0m, null));
        // เส้น "หักเงินมัดจำจากยอดชำระ" (ApplyDeposit) — เป็นแถวคนละแถว
        Assert.False(DepositPolicyResolver.BillDeductionIsTaxedDeposit(100m, 2000m, "REC-0001"));
        Assert.False(DepositPolicyResolver.BillDeductionIsTaxedDeposit(0m, 0m, "TIV-2026-0001"));
    }

    [Fact]
    public void ตัวเลือกเรียงตามเจ้าของ_และคำเตือนบริการมีเฉพาะโหมดที่ไม่ใช่VATทันที()
    {
        var o = DepositPolicyResolver.Options;
        Assert.Equal(new[] { "FullDeposit", "VatPendingUndue", "VatImmediate" }, o.Select(x => x.Value).ToArray());
        Assert.NotNull(o[0].ServiceWarning);
        Assert.NotNull(o[1].ServiceWarning);
        Assert.Null(o[2].ServiceWarning);
        Assert.All(o, x => Assert.False(string.IsNullOrWhiteSpace(x.LegalReference)));
    }

    // ═══ P0-3 / P0-1 (ด่านใน DocumentService ใช้ตัวตัดสินนี้ตัวเดียว) ═══

    [Fact]
    public void มัดจำออกใบกำกับแล้ว_ห้ามหักเต็มจำนวน_มัดจำพักVATหรือเต็มยอดหักได้()
    {
        Assert.True(DepositPolicyResolver.GrossApplyBlocked(130.84m, depositVatPending: false));   // VAT ทันที
        Assert.False(DepositPolicyResolver.GrossApplyBlocked(130.84m, depositVatPending: true));   // รอเรียกเก็บ
        Assert.False(DepositPolicyResolver.GrossApplyBlocked(0m, depositVatPending: true));        // เต็มยอด
        Assert.False(DepositPolicyResolver.GrossApplyBlocked(0m, depositVatPending: false));       // บริษัทไม่จด VAT
        var msg = DepositPolicyResolver.GrossApplyBlockedMessage("TIV-0001");
        Assert.Contains("TIV-0001", msg);
        Assert.Contains("①", msg);
        Assert.Contains("②", msg);
    }

    [Fact]
    public void หักมัดจำแบบขับJE_ใช้ได้เฉพาะชนิดที่AutoPostอ่านธง()
    {
        Assert.True(DepositPolicyResolver.DrivesJournalSupported(DocumentType.Receipt, false));
        Assert.True(DepositPolicyResolver.DrivesJournalSupported(DocumentType.ReceiptVoucher, false));
        Assert.True(DepositPolicyResolver.DrivesJournalSupported(DocumentType.TaxInvoice, issuedAsCashReceipt: true));
        // ใบเครดิต — เดิมรับธงแล้วไม่มีผล (เช็คเอาต์ที่พักเก็บเงินเต็ม 7,450 ซ้ำกับมัดจำ)
        Assert.False(DepositPolicyResolver.DrivesJournalSupported(DocumentType.TaxInvoice, issuedAsCashReceipt: false));
        Assert.False(DepositPolicyResolver.DrivesJournalSupported(DocumentType.Invoice, false));
    }

    // ═══ C1 รอบ 193 หลังฝ่ายค้าน — เส้นขับ JE (ใบขายเงินสดของคู่ค้า) บล็อกเฉพาะงวดมัดจำยื่นแล้ว ═══

    [Fact]
    public void เส้นขับJE_มัดจำออกใบกำกับแล้ว_งวดยังไม่ยื่น_ลงได้พร้อมธง_ไม่ถอยไปตั้งหนี้()
    {
        Assert.Equal(DrivesGrossApplyVerdict.AllowedVatMoved,
            DepositPolicyResolver.DrivesGrossApply(130.84m, depositVatPending: false, depositVatPeriodDeclared: false));
        var note = DepositPolicyResolver.DrivesVatMovedNote("TIV-0001", 130.84m);
        Assert.Contains("TIV-0001", note);
        Assert.Contains("130.84", note);
        Assert.Contains(DepositPolicyResolver.ImmediateVatGrossApplyRuleCode, note);
    }

    [Fact]
    public void เส้นขับJE_มัดจำออกใบกำกับแล้ว_งวดยื่นแล้ว_บล็อก()
        => Assert.Equal(DrivesGrossApplyVerdict.Blocked,
            DepositPolicyResolver.DrivesGrossApply(130.84m, depositVatPending: false, depositVatPeriodDeclared: true));

    [Theory]
    [InlineData(130.84, true, true)]    // VAT พัก 21913 — ยังไม่เคยรายงาน
    [InlineData(0, false, true)]        // เต็มยอด/ไม่จด VAT
    [InlineData(0, true, false)]
    public void เส้นขับJE_มัดจำไม่มีVATที่รายงานแล้ว_ลงได้ตามปกติไม่ว่างวดไหน(double vat, bool pending, bool declared)
        => Assert.Equal(DrivesGrossApplyVerdict.Allowed,
            DepositPolicyResolver.DrivesGrossApply((decimal)vat, pending, declared));
}
