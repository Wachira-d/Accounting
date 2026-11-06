using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 249 — สินทรัพย์ 1 ชิ้น ↔ เอกสารซื้อหลายใบ (erp-review/2026-11-06/ASSET_MULTI_DOCUMENT_DESIGN.md) ·
/// ล็อกตัวตัดสินเดียว <see cref="AssetCostSourceRules"/> + สูตรยอด GL ของบรรทัด <see cref="PurchaseLineGlAmount"/>
/// (ทั้งทิศ "ใบที่ต้องผ่าน" และ "ใบที่ต้องถูกกัน")
/// </summary>
public class AssetCostSourceRulesTests
{
    // ───── บทบาทผัง ─────

    [Theory]
    [InlineData("11810", AssetCostSourceRules.AccountRole.Deposit)]
    [InlineData("118101", AssetCostSourceRules.AccountRole.Deposit)]
    [InlineData("11820", AssetCostSourceRules.AccountRole.Deposit)]
    [InlineData("118", AssetCostSourceRules.AccountRole.Deposit)]
    [InlineData("12280", AssetCostSourceRules.AccountRole.ConstructionInProgress)]
    [InlineData("12260", AssetCostSourceRules.AccountRole.Asset)]
    [InlineData("12290", AssetCostSourceRules.AccountRole.Asset)]
    [InlineData("122601", AssetCostSourceRules.AccountRole.Asset)]
    public void ผังมัดจำ_งานระหว่างก่อสร้าง_สินทรัพย์_ผูกได้(string code, AssetCostSourceRules.AccountRole expected)
        => Assert.Equal(expected, AssetCostSourceRules.RoleOf(code));

    [Theory]
    [InlineData("11830")]   // เงินมัดจำรับล่วงหน้า (สุทธิ) — ฝั่งรับ ไม่ใช่มัดจำที่เราจ่าย
    [InlineData("1181")]    // ไม่ใช่ผังย่อยของ 118 ตรงตัว
    [InlineData("51800")]   // ค่าใช้จ่าย — ต้องแก้ผังก่อน (§65 ตรี(5))
    [InlineData("12410")]   // ค่าใช้จ่ายรอตัดจ่าย ไม่ใช่สินทรัพย์ถาวร
    [InlineData("")]
    [InlineData(null)]
    public void ผังอื่นผูกไม่ได้(string? code)
        => Assert.Equal(AssetCostSourceRules.AccountRole.NotEligible, AssetCostSourceRules.RoleOf(code));

    // ───── ชนิดเอกสาร / เครื่องหมาย ─────

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice, null, 1)]
    [InlineData(DocumentType.Expense, null, 1)]
    [InlineData(DocumentType.PaymentVoucher, null, 1)]
    [InlineData(DocumentType.CertificateInLieu, null, 1)]
    [InlineData(DocumentType.CreditNote, true, -1)]
    [InlineData(DocumentType.DebitNote, true, 1)]
    public void เอกสารฝั่งซื้อมีเครื่องหมาย(DocumentType t, bool? purchaseSide, int expected)
        => Assert.Equal(expected, AssetCostSourceRules.SignFor(t, purchaseSide));

    [Theory]
    [InlineData(DocumentType.CreditNote, false)]
    [InlineData(DocumentType.CreditNote, null)]      // ไม่รู้ฝั่ง = ไม่รับ (ไม่เดา)
    [InlineData(DocumentType.Invoice, null)]
    [InlineData(DocumentType.PurchaseOrder, null)]
    [InlineData(DocumentType.GoodsReceiptNote, null)]
    public void เอกสารอื่นไม่ใช่ต้นทุน(DocumentType t, bool? purchaseSide)
        => Assert.Null(AssetCostSourceRules.SignFor(t, purchaseSide));

    // ───── ยอดต่อบรรทัด (VAT) ─────

    [Fact]
    public void VAT_ที่เคลมได้ไม่รวมเป็นต้นทุน()
    {
        var c = AssetCostSourceRules.CostOf(300_000m, 21_000m, isVatClaimable: true, exchangeRate: 1m, sign: 1);
        Assert.Equal(300_000m, c.Amount);
        Assert.Equal(0m, c.NonClaimableVat);
    }

    [Fact]
    public void VAT_ต้องห้าม_รถยนต์นั่ง_รวมเป็นต้นทุน()
    {
        // §82/5(6) รถยนต์นั่ง ≤ 10 ที่นั่ง — ใบลง Dr 12240 = ฐาน + VAT ⇒ ต้นทุนผูก = ยอดเดียวกัน (ไม่ค้างผังต้นทางหลังโอน)
        var c = AssetCostSourceRules.CostOf(900_000m, 63_000m, isVatClaimable: false, exchangeRate: 1m, sign: 1);
        Assert.Equal(963_000m, c.Amount);
        Assert.Equal(63_000m, c.NonClaimableVat);
        Assert.Equal(963_000m, PurchaseLineGlAmount.Standalone(900_000m, 63_000m, false));
    }

    [Fact]
    public void ใบลดหนี้ติดลบ_และแปลงบาทตามอัตราของใบ()
    {
        var c = AssetCostSourceRules.CostOf(1_000m, 70m, true, exchangeRate: 35.5m, sign: -1);
        Assert.Equal(-35_500m, c.Amount);
    }

    // ───── กันคิดต้นทุนซ้ำ ─────

    [Fact]
    public void ผูกบรรทัดเดียวกันเกินยอดไม่ได้()
    {
        var p = AssetCostSourceRules.AllocationProblem(300_000m, alreadyLinkedAbs: 300_000m, requestedAbs: 1m, "PV-001 มัดจำ");
        Assert.NotNull(p);
        Assert.Contains("เหลือผูกได้ 0.00", p);
    }

    [Fact]
    public void แบ่งมัดจำก้อนเดียวให้สองสินทรัพย์ได้_ทิศตรงข้าม()
    {
        Assert.Null(AssetCostSourceRules.AllocationProblem(300_000m, 0m, 120_000m, "x"));
        Assert.Null(AssetCostSourceRules.AllocationProblem(300_000m, 120_000m, 180_000m, "x"));   // พอดีเป๊ะผ่าน
        Assert.NotNull(AssetCostSourceRules.AllocationProblem(300_000m, 120_000m, 180_000.01m, "x"));
        Assert.Equal(180_000m, AssetCostSourceRules.Remaining(300_000m, 120_000m));
        Assert.Equal(0m, AssetCostSourceRules.Remaining(300_000m, 400_000m));
    }

    [Fact]
    public void ยอดผูกต้องมากกว่าศูนย์()
    {
        Assert.NotNull(AssetCostSourceRules.AllocationProblem(1_000m, 0m, 0m, "x"));
        Assert.NotNull(AssetCostSourceRules.AllocationProblem(1_000m, 0m, -5m, "x"));
    }

    [Fact]
    public void ยอดผูกเกินสองตำแหน่งไม่รับ()
        => Assert.NotNull(AssetCostSourceRules.AllocationProblem(1_000m, 0m, 10.005m, "x"));

    // ───── ด่านบรรทัด ─────

    [Fact]
    public void บรรทัดผังค่าใช้จ่ายถูกกัน_พร้อมทางไปต่อ()
    {
        var p = AssetCostSourceRules.LineProblem("PV-1", DocumentType.PaymentVoucher, 1, true, false, false,
            AssetCostSourceRules.AccountRole.NotEligible, "51800", 1_000m);
        Assert.NotNull(p);
        Assert.Contains("แก้ผังบัญชี", p);
        Assert.Contains("65 ตรี(5)", p);
    }

    [Fact]
    public void ใบยังไม่อนุมัติ_ใบตัดชำระ_ใบอ้างใบรับสินค้า_ถูกกัน()
    {
        var dep = AssetCostSourceRules.AccountRole.Deposit;
        Assert.NotNull(AssetCostSourceRules.LineProblem("PV-1", DocumentType.PaymentVoucher, 1, false, false, false, dep, "11810", 1m));
        Assert.NotNull(AssetCostSourceRules.LineProblem("PV-1", DocumentType.PaymentVoucher, 1, true, true, false, dep, "11810", 1m));
        Assert.NotNull(AssetCostSourceRules.LineProblem("PI-1", DocumentType.PurchaseInvoice, 1, true, false, true, dep, "11810", 1m));
        Assert.NotNull(AssetCostSourceRules.LineProblem("INV-1", DocumentType.Invoice, null, true, false, false, dep, "11810", 1m));
        // ทิศตรงข้าม: ใบมัดจำที่อนุมัติแล้วผ่าน
        Assert.Null(AssetCostSourceRules.LineProblem("PV-1", DocumentType.PaymentVoucher, 1, true, false, false, dep, "11810", 1m));
    }

    [Fact]
    public void ร่างอัตโนมัติรวมเข้าได้เฉพาะที่ยังไม่ยืนยันและไม่มีค่าเสื่อม()
    {
        Assert.True(AssetCostSourceRules.CanAbsorbLegacyDraft(true, AssetStatus.Active, 0m, false, false, 0));
        Assert.False(AssetCostSourceRules.CanAbsorbLegacyDraft(false, AssetStatus.Active, 0m, false, false, 0));   // ยืนยันแล้ว
        Assert.False(AssetCostSourceRules.CanAbsorbLegacyDraft(true, AssetStatus.Active, 10m, false, false, 0));   // มีค่าเสื่อม
        Assert.False(AssetCostSourceRules.CanAbsorbLegacyDraft(true, AssetStatus.Active, 0m, true, false, 0));
        Assert.False(AssetCostSourceRules.CanAbsorbLegacyDraft(true, AssetStatus.Active, 0m, false, true, 0));     // เป็นสินทรัพย์หลายใบเอง
        Assert.False(AssetCostSourceRules.CanAbsorbLegacyDraft(true, AssetStatus.Active, 0m, false, false, 2));
    }

    // ───── ตาข่าย "ผูกเงินที่ไม่มีใน GL" ─────

    [Fact]
    public void ผูกเกินยอดที่ใบลงผังจริง_นับเป็นส่วนเกิน_พอดีไม่นับ()
    {
        Guid dep = Guid.NewGuid(), cash = Guid.NewGuid();
        var gl = new[] { new AssetCostSourceRules.AccountAmount(dep, 300_000m), new AssetCostSourceRules.AccountAmount(cash, -300_000m) };
        Assert.Equal(0m, AssetCostSourceRules.ExcessOverGl(gl, new[] { new AssetCostSourceRules.AccountAmount(dep, 300_000m) }, 1));
        Assert.Equal(50m, AssetCostSourceRules.ExcessOverGl(gl, new[] { new AssetCostSourceRules.AccountAmount(dep, 300_050m) }, 1));
        // ใบสำคัญจ่ายที่ตัดชำระ (บรรทัดไม่ลงผังของตัวเอง) — GL ไม่มี 118xx เลย ⇒ ผูกเท่าไรก็เกินทั้งหมด
        Assert.Equal(1_000m, AssetCostSourceRules.ExcessOverGl(Array.Empty<AssetCostSourceRules.AccountAmount>(),
            new[] { new AssetCostSourceRules.AccountAmount(dep, 1_000m) }, 1));
    }

    [Fact]
    public void ใบลดหนี้_เทียบด้วยเครื่องหมายของใบ()
    {
        var mach = Guid.NewGuid();
        var gl = new[] { new AssetCostSourceRules.AccountAmount(mach, -20_400m) };
        Assert.Equal(0m, AssetCostSourceRules.ExcessOverGl(gl, new[] { new AssetCostSourceRules.AccountAmount(mach, -20_400m) }, -1));
        Assert.Equal(0m, AssetCostSourceRules.ExcessOverGl(gl, new[] { new AssetCostSourceRules.AccountAmount(mach, -10_000m) }, -1));   // ผูกบางส่วนได้
        Assert.Equal(100m, AssetCostSourceRules.ExcessOverGl(gl, new[] { new AssetCostSourceRules.AccountAmount(mach, -20_500m) }, -1));
    }

    // ───── JE โอน ─────

    [Fact]
    public void JE_โอน_มัดจำ_งานระหว่างก่อสร้าง_เข้าเครื่องจักร_ผังเดียวกันไม่ลง()
    {
        Guid dep = Guid.NewGuid(), cip = Guid.NewGuid(), mach = Guid.NewGuid();
        var legs = AssetCostSourceRules.TransferLegs(mach, "12260", new[]
        {
            new AssetCostSourceRules.Source(dep, "11810", 300_000m),
            new AssetCostSourceRules.Source(cip, "12280", 400_000m),
            new AssetCostSourceRules.Source(mach, "12260", 300_000m),
            new AssetCostSourceRules.Source(mach, "12260", 20_000m),
        });
        Assert.Equal(3, legs.Count);
        Assert.Equal(700_000m, legs.Single(l => l.AccountId == mach).Debit);
        Assert.Equal(300_000m, legs.Single(l => l.AccountId == dep).Credit);
        Assert.Equal(400_000m, legs.Single(l => l.AccountId == cip).Credit);
        Assert.Equal(legs.Sum(l => l.Debit), legs.Sum(l => l.Credit));
    }

    [Fact]
    public void ทุกใบอยู่ผังปลายทางแล้ว_ไม่ต้องมี_JE()
    {
        var mach = Guid.NewGuid();
        Assert.Empty(AssetCostSourceRules.TransferLegs(mach, "12260", new[]
        {
            new AssetCostSourceRules.Source(mach, "12260", 500_000m),
            new AssetCostSourceRules.Source(mach, "12260", -10_000m),
        }));
    }

    [Fact]
    public void ใบลดหนี้บนผังงานระหว่างก่อสร้าง_กลับขา_และยังสมดุล()
    {
        Guid cip = Guid.NewGuid(), mach = Guid.NewGuid(), dep = Guid.NewGuid();
        var legs = AssetCostSourceRules.TransferLegs(mach, "12260", new[]
        {
            new AssetCostSourceRules.Source(dep, "11810", 100m),
            new AssetCostSourceRules.Source(cip, "12280", -150m),
        });
        Assert.Equal(150m, legs.Single(l => l.AccountId == cip).Debit);
        Assert.Equal(100m, legs.Single(l => l.AccountId == dep).Credit);
        Assert.Equal(50m, legs.Single(l => l.AccountId == mach).Credit);
        Assert.Equal(legs.Sum(l => l.Debit), legs.Sum(l => l.Credit));
    }

    // ───── โอนเป็นสินทรัพย์พร้อมใช้ ─────

    private static readonly DateTime Today = new(2026, 11, 6);

    private static string? Place(bool under = true, int active = 3, int voided = 0, int inelig = 0, decimal cost = 1_020_000m,
        decimal salvage = 0m, AssetCostSourceRules.AccountRole role = AssetCostSourceRules.AccountRole.Asset, bool depreciable = true,
        DepreciationMethod m = DepreciationMethod.StraightLine, int life = 120, DateTime? date = null)
        => AssetCostSourceRules.PlaceInServiceProblem(under, active, voided, inelig, cost, salvage, role, depreciable, m, life, date ?? Today, Today);

    [Fact]
    public void โอนได้เมื่อครบเงื่อนไข() => Assert.Null(Place());

    [Fact]
    public void โอนซ้ำไม่ได้() => Assert.NotNull(Place(under: false));

    [Fact]
    public void มีใบต้นทางถูกยกเลิก_ต้องปลดก่อน() => Assert.Contains("ยกเลิก", Place(voided: 1));

    [Fact]
    public void บรรทัดถูกย้ายไปผังค่าใช้จ่าย_ต้องแก้ก่อน() => Assert.NotNull(Place(inelig: 1));

    [Fact]
    public void ผังปลายทางเป็นงานระหว่างก่อสร้าง_ไม่ใช่การโอน()
        => Assert.NotNull(Place(role: AssetCostSourceRules.AccountRole.ConstructionInProgress));

    [Fact]
    public void ที่ดินต้องไม่คิดค่าเสื่อม_และทิศตรงข้ามผ่าน()
    {
        Assert.NotNull(Place(depreciable: false));
        Assert.Null(Place(depreciable: false, m: DepreciationMethod.None, life: 0));
    }

    [Fact]
    public void วันพร้อมใช้ในอนาคตไม่ได้_วันนี้ได้()
    {
        Assert.NotNull(Place(date: Today.AddDays(1)));
        Assert.Null(Place(date: Today));
    }

    [Fact]
    public void ซากต้องน้อยกว่าต้นทุน_ต้นทุนต้องบวก()
    {
        Assert.NotNull(Place(salvage: 1_020_000m));
        Assert.NotNull(Place(cost: 0m));
        Assert.NotNull(Place(active: 0));
        Assert.NotNull(Place(life: 0));
    }

    // ───── ปรับต้นทุนหลังโอน ─────

    [Fact]
    public void ใบมาก่อนมีค่าเสื่อม_สร้างตารางใหม่ทั้งก้อน()
    {
        var a = AssetCostSourceRules.Adjust(1_020_000m, 1_020_000m, -10_000m, 0m, 120, 0);
        Assert.Equal(1_010_000m, a.NewCost);
        Assert.Equal(1_010_000m, a.NewNetBookValue);
        Assert.Null(a.DepreciableBase);
        Assert.Null(a.EffectiveFromMonthIndex);
    }

    [Fact]
    public void ใบมาหลังมีค่าเสื่อม_ปรับไปข้างหน้า_งวดที่ลงแล้วไม่แตะ()
    {
        // ทุน 1,200,000 · 120 เดือน · ลงแล้ว 12 งวด (NBV 1,080,000) · ค่าติดตั้งตามมา 21,600
        var a = AssetCostSourceRules.Adjust(1_200_000m, 1_080_000m, 21_600m, 0m, 120, 12);
        Assert.Equal(1_221_600m, a.NewCost);
        Assert.Equal(1_101_600m, a.NewNetBookValue);
        Assert.Equal(1_101_600m, a.DepreciableBase);
        Assert.Equal(108, a.RemainingLifeMonths);
        Assert.Equal(12, a.EffectiveFromMonthIndex);
    }

    [Fact]
    public void ลดหนี้จน_NBV_ต่ำกว่าซาก_ถูกกัน_ทิศตรงข้ามผ่าน()
    {
        Assert.NotNull(AssetCostSourceRules.AdjustmentProblem(100_000m, -95_000m, 10_000m, DepreciationMethod.StraightLine, 60, 10));
        Assert.Null(AssetCostSourceRules.AdjustmentProblem(100_000m, -90_000m, 10_000m, DepreciationMethod.StraightLine, 60, 10));
    }

    [Fact]
    public void ครบอายุแล้วปรับต้นทุนไม่ได้()
        => Assert.NotNull(AssetCostSourceRules.AdjustmentProblem(1m, 500m, 1m, DepreciationMethod.StraightLine, 60, 60));

    // ───── ปลด / ยกเลิกใบ / แก้ผัง ─────

    [Fact]
    public void ปลดได้เฉพาะแถวที่ยังไม่โอน_และต้องมีเหตุผล()
    {
        Assert.NotNull(AssetCostSourceRules.ReleaseProblem(capitalized: true, false, "ผูกผิด"));
        Assert.NotNull(AssetCostSourceRules.ReleaseProblem(false, alreadyReleased: true, "ผูกผิด"));
        Assert.NotNull(AssetCostSourceRules.ReleaseProblem(false, false, "  "));
        Assert.Null(AssetCostSourceRules.ReleaseProblem(false, false, "ผูกผิดตัว"));
    }

    [Fact]
    public void ยกเลิกใบที่โอนแล้วถูกกัน_ใบที่ไม่ผูกผ่าน()
    {
        Assert.Contains("ใบลดหนี้", AssetCostSourceRules.VoidBlock("PV-1", new[] { "FA-202611-0001" }));
        Assert.Null(AssetCostSourceRules.VoidBlock("PV-1", Array.Empty<string>()));
        Assert.NotNull(AssetCostSourceRules.LineChangeBlock(new[] { "FA-1" }));
        Assert.Null(AssetCostSourceRules.LineChangeBlock(Array.Empty<string>()));
    }

    [Theory]
    [InlineData(DocumentType.CreditNote, AssetCostSourceRules.AccountRole.Asset, "x", AssetCostSourceKind.CreditNote)]
    [InlineData(DocumentType.PaymentVoucher, AssetCostSourceRules.AccountRole.Deposit, "มัดจำ 30%", AssetCostSourceKind.Deposit)]
    [InlineData(DocumentType.PurchaseInvoice, AssetCostSourceRules.AccountRole.ConstructionInProgress, "งวดที่ 2", AssetCostSourceKind.Progress)]
    [InlineData(DocumentType.PurchaseInvoice, AssetCostSourceRules.AccountRole.Asset, "ค่าติดตั้ง", AssetCostSourceKind.Auxiliary)]
    [InlineData(DocumentType.PurchaseInvoice, AssetCostSourceRules.AccountRole.Asset, "เครื่องจักร CNC", AssetCostSourceKind.Final)]
    public void ป้ายชนิด(DocumentType t, AssetCostSourceRules.AccountRole r, string d, AssetCostSourceKind k)
        => Assert.Equal(k, AssetCostSourceRules.KindFor(t, r, d));
}
