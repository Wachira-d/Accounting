using System;
using System.Linq;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม U 2026-10-26 (ผู้ใช้รายงาน · ใบ Shopee INV2026090199) — <see cref="PostVatDeduction"/>: "หักหลัง VAT" ลดยอดชำระโดยไม่แตะฐาน/VAT
///
/// <para><b>ครึ่งที่ 1</b> (ใบที่เคยผิด — PV-202610-0040): ฐาน 402.93 · VAT 35.07 ⇒ ด่าน VAT บล็อก (ข้อความบอกฐานที่ VAT บอก 501.00 และช่อง "หักหลัง VAT") ·
/// ใบที่ถูก 500.93 / 35.07 / หัก 98 ⇒ ยอดชำระ 438 · ขา JE Cr 98 · ขาเงินสดลด 98 (DocumentCashDelta) ⇒ JE สมดุล</para>
/// <para><b>ครึ่งที่ 2</b> (ทิศตรงข้าม — ห้ามแตะ): ใบปกติไม่มีหัก ⇒ ยอดชำระจริงตามเดิม · ผลต่างปัดเศษสตางค์ผ่าน · หลายอัตรา/ยกเว้น/0% ผ่าน ·
/// ใบชำระบางส่วน (ยอดชำระจริงอื่น) ไม่ถูกบล็อกเมื่อไม่มีหัก · ฝั่งขาย/ติดลบ/เกินยอด = ปฏิเสธพร้อมทางไปต่อ</para>
/// </summary>
public class PostVatDeductionTests
{
    // ── ครึ่งที่ 1: ใบ Shopee ──

    [Fact]
    public void Shopee_ยอดชำระ_เท่ากับรวมทั้งสิ้นลบหักหลังVAT()
        => Assert.Equal(438.00m, PostVatDeduction.AmountDue(536.00m, 98.00m));

    [Fact]
    public void Shopee_ไม่ระบุยอดชำระจริง_ระบบตั้งให้เท่ายอดที่ต้องชำระ()
    {
        var r = PostVatDeduction.Resolve(536.00m, 98.00m, null);
        Assert.Null(r.Error);
        Assert.Equal(438.00m, r.ActualPaidAmount);
        Assert.Equal(438.00m, r.AmountDue);
    }

    [Fact]
    public void Shopee_ระบุยอดชำระจริงตรง_ผ่าน()
    {
        var r = PostVatDeduction.Resolve(536.00m, 98.00m, 438.00m);
        Assert.Null(r.Error);
        Assert.Equal(438.00m, r.ActualPaidAmount);
    }

    [Fact]
    public void ด่านข้อ4_ฐานบวกVATลบหัก_ไม่เท่ายอดชำระ_ปฏิเสธพร้อมทางไปต่อ()
    {
        var r = PostVatDeduction.Resolve(536.00m, 98.00m, 400.00m);
        Assert.NotNull(r.Error);
        Assert.Contains("438.00", r.Error);
        Assert.Contains("หักหลัง VAT", r.Error);
        Assert.Contains("ยอดชำระจริง", r.Error);
    }

    [Fact]
    public void Shopee_ขาJE_Cr98_และขาเงินสดลด98_สมดุลผ่านด่านเดิม()
    {
        var (dr, cr) = PostVatDeduction.JournalLine(98.00m);
        Assert.Equal((0m, 98.00m), (dr, cr));
        // ขาเงินสดของ PV ที่จ่ายในตัว: ยอดเอกสาร 536 · ยอดชำระจริง 438 ⇒ Dr เงินสดกลับ 98
        var delta = PaymentSettlementAdjustment.DocumentCashDelta(DocumentType.PaymentVoucher, PaymentType.Cash, false, false, 536.00m, 438.00m);
        Assert.Equal(98.00m, delta);
        // adjusting Dr 0 − Cr 98 + ส่วนต่าง 98 = 0 ⇒ ด่านสมดุลเดิมผ่าน
        Assert.Null(PaymentSettlementAdjustment.CheckDocumentLines(delta, dr, cr, 536.00m, 438.00m));
    }

    [Fact]
    public void PV_202610_0040_ฐานถูกบีบ_402_93_กับVAT_35_07_ถูกบล็อก()
    {
        var err = PostVatDeduction.VatConsistencyError(new[] { new VatLineFact(402.93m, 7m, 35.07m) });
        Assert.NotNull(err);
        Assert.Contains("35.07", err);
        Assert.Contains("402.93", err);
        Assert.Contains("28.21", err);          // 402.93 × 7% ที่ควรเป็น
        Assert.Contains("501.00", err);         // ฐานที่ VAT 35.07 บอก (35.07 ÷ 7%)
        Assert.Contains("หักหลัง VAT", err);    // ทางไปต่อ
    }

    [Fact]
    public void Shopee_ฐานถูก_500_93_กับVAT_35_07_ผ่าน()
        => Assert.Null(PostVatDeduction.VatConsistencyError(new[] { new VatLineFact(500.93m, 7m, 35.07m) }));

    [Fact]
    public void ผังสำรอง_ใช้ผังของบรรทัดยอดสูงสุด()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var picked = PostVatDeduction.FallbackAccountIdFromLines(new (Guid?, decimal)[] { (a, 100m), (b, 400.93m), (null, 9999m) });
        Assert.Equal(b, picked);
        Assert.Null(PostVatDeduction.FallbackAccountIdFromLines(new (Guid?, decimal)[] { (null, 1m) }));
    }

    // ── ครึ่งที่ 2: ทิศตรงข้าม — ใบเดิมทุกแบบไม่ถูกแตะ ──

    [Theory]
    [InlineData(null)]
    [InlineData(536.00)]
    [InlineData(300.00)]   // ชำระบางส่วน (ยอดชำระจริงต่าง) — ไม่มีหัก ⇒ ไม่บล็อก (บรรทัดปรับเดิมดูแล)
    public void ไม่มีหัก_ยอดชำระจริงตามเดิมทุกค่า(double? actualPaid)
    {
        var ap = actualPaid is double v ? (decimal?)(decimal)v : null;
        var r = PostVatDeduction.Resolve(536.00m, 0m, ap);
        Assert.Null(r.Error);
        Assert.Equal(ap, r.ActualPaidAmount);
    }

    [Fact]
    public void ด่านVAT_ผลต่างปัดเศษสตางค์_และหลายอัตรา_และยกเว้น_ผ่าน()
    {
        // เศษปัดสะสม 0.01–0.03 (Lazada 4,695.33 × 7% = 328.67) · บรรทัด 0% / ยกเว้น (VAT 0) ไม่นับ · บรรทัดอัตรา 7 ที่ VAT 0 (ภาษีซื้อไม่มี) ไม่นับ
        var lines = new[]
        {
            new VatLineFact(4695.33m, 7m, 328.68m),   // ต่าง 0.01 — เศษปัด
            new VatLineFact(1000.00m, 0m, 0m),
            new VatLineFact(250.00m, 7m, 0m),
            new VatLineFact(2000.00m, 10m, 200.00m),  // อัตราอื่นแยกกลุ่ม
        };
        Assert.Null(PostVatDeduction.VatConsistencyError(lines));
        Assert.Null(PostVatDeduction.VatConsistencyError(Array.Empty<VatLineFact>()));
    }

    [Fact]
    public void ด่านVAT_หลายบรรทัดรวมกลุ่มเดียว_ตรงยอดกลุ่มผ่าน_แม้ต่อบรรทัดปัดคนละทาง()
    {
        // 3 บรรทัด ฐาน 33.33 ×3 = 99.99 · VAT กลุ่ม = 7.00 (เกลี่ย 2.33/2.33/2.34) — เครื่องคิดเลข 99.99 × 7% = 7.00
        var lines = new[] { new VatLineFact(33.33m, 7m, 2.33m), new VatLineFact(33.33m, 7m, 2.33m), new VatLineFact(33.33m, 7m, 2.34m) };
        Assert.Null(PostVatDeduction.VatConsistencyError(lines));
    }

    [Theory]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Receipt)]
    public void ฝั่งขาย_ปฏิเสธพร้อมบอกทางไปต่อSettlement(DocumentType type)
    {
        var err = PostVatDeduction.ValidateAmount(98m, 536m, type);
        Assert.NotNull(err);
        Assert.Contains("Settlement", err);
    }

    [Theory]
    [InlineData(DocumentType.PaymentVoucher)]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    public void ฝั่งซื้อ_ค่าที่ถูกต้องผ่าน_ศูนย์ผ่านทุกชนิด(DocumentType type)
    {
        Assert.Null(PostVatDeduction.ValidateAmount(98.00m, 536.00m, type));
        Assert.Null(PostVatDeduction.ValidateAmount(0m, 536.00m, type));
        Assert.Null(PostVatDeduction.ValidateAmount(0m, 536.00m, DocumentType.Invoice));
    }

    [Fact]
    public void ค่าผิด_ติดลบ_เกินยอด_ทศนิยม3ตำแหน่ง_ปฏิเสธ()
    {
        Assert.NotNull(PostVatDeduction.ValidateAmount(-1m, 536m, DocumentType.PaymentVoucher));
        Assert.NotNull(PostVatDeduction.ValidateAmount(536.01m, 536m, DocumentType.PaymentVoucher));
        Assert.NotNull(PostVatDeduction.ValidateAmount(98.001m, 536m, DocumentType.PaymentVoucher));
    }

    [Fact]
    public void ผังหัก_ห้ามผังเงินสด()
    {
        Assert.NotNull(PostVatDeduction.AccountError("11110"));
        Assert.Null(PostVatDeduction.AccountError("51150"));
        Assert.Null(PostVatDeduction.AccountError("52120"));
    }

    [Fact]
    public void ค่าเริ่มต้นผัง_ตัวเดียวกับบรรทัดปรับตอนชำระ_51150()
    {
        Assert.Equal(PaymentSettlementAdjustment.PurchaseDiscountAccountCode, PostVatDeduction.DefaultAccountCode);
        Assert.Equal(PaymentSettlementAdjustment.MatchTolerance, PostVatDeduction.Tolerance);
        var chart = Accounting.Services.ChartOfAccountTemplates.GetCommonAccounts();
        Assert.Contains("ส่วนลดรับ", chart.Single(a => a.Code == PostVatDeduction.DefaultAccountCode).NameTh);
    }
}
