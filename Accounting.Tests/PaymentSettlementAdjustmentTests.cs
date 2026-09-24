using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 (คำตัดสินเจ้าของข้อ 1 · 3 · 4) — <see cref="PaymentSettlementAdjustment"/>: ส่วนต่าง "ยอดตามใบกำกับ ↔ เงินที่จ่ายจริง"
/// ลงที่ขั้นชำระ ยอดเอกสารคงตามใบกำกับ
///
/// <para><b>ครึ่งที่ 1</b> (ใบที่เคยผิด): Shopee ใบกำกับ 536 · จ่าย 438 · ค่าส่ง +37 → 51120 · คูปอง −135 → 51150 ⇒ ปิดหนี้ 536 พอดี ·
/// JE ทั้งสองทางเข้า (ชำระใบตั้งหนี้ · ใบสำคัญจ่ายที่จ่ายในตัว) สมดุล · ภาษีซื้อ 35.07 ไม่ถูกแตะ</para>
/// <para><b>ครึ่งที่ 2</b> (ห้ามแตะ): ไม่มีบรรทัดปรับ = ยอดที่ปิด = เงินที่จ่าย (พฤติกรรมเดิม) · ใบที่ไม่ได้ระบุยอดชำระจริง / ใบตั้งหนี้ /
/// ใบเงินเชื่อ / บริการต่างประเทศ ⇒ ส่วนต่างขาเงินสด 0 · adjusting lines ที่ไม่มีส่วนต่างยังต้อง net-zero ตามกติกาเดิม (ข้อความเดิม)</para>
/// </summary>
public class PaymentSettlementAdjustmentTests
{
    private static readonly SettlementAdjustmentLine[] ShopeeLines =
    {
        new(PaymentSettlementAdjustment.FreightInAccountCode, 37.00m, "ค่าจัดส่ง"),
        new(PaymentSettlementAdjustment.PurchaseDiscountAccountCode, -135.00m, "Shopee Voucher"),
    };

    // ── ครึ่งที่ 1 ──────────────────────────────────────────────────────────

    [Fact]
    public void Shopee_จ่าย438_บวกค่าส่ง37_หักคูปอง135_ปิดหนี้536พอดี()
    {
        var c = PaymentSettlementAdjustment.Check(438.00m, 536.00m, ShopeeLines);

        Assert.True(c.Ok, c.Error);
        Assert.Equal(536.00m, c.SettledAmount);
        Assert.Equal(98.00m, c.AdjustmentNet);   // หนี้ที่ปิดโดยไม่ใช่เงินสด
    }

    [Fact]
    public void Shopee_JEการชำระใบตั้งหนี้_สมดุล_เงินสดออก438()
    {
        var c = PaymentSettlementAdjustment.Check(438.00m, 536.00m, ShopeeLines);
        // ขาเดียวกับ DocumentService.CreatePaymentJournalAsync ฝั่งซื้อ: Dr เจ้าหนี้ (เงิน + หนี้ที่ปิดด้วยบรรทัดปรับ) · Cr เงินสด · บรรทัดปรับ
        decimal dr = 438.00m + c.AdjustmentNet, cr = 438.00m;
        foreach (var l in ShopeeLines)
        {
            var (d, k) = PaymentSettlementAdjustment.JournalSide(l.Amount);
            dr += d; cr += k;
        }
        Assert.Equal(573.00m, dr);   // เจ้าหนี้ 536 + ค่าขนส่ง 37
        Assert.Equal(573.00m, cr);   // เงินสด 438 + ส่วนลดรับ 135
    }

    [Fact]
    public void Shopee_ใบสำคัญจ่ายจ่ายในตัว_ขาเงินสดลดลง98_บรรทัดปรับอธิบายได้พอดี_JEสมดุล()
    {
        var delta = PaymentSettlementAdjustment.DocumentCashDelta(
            DocumentType.PaymentVoucher, PaymentType.Cash, isForeignService: false, 536.00m, 438.00m);
        Assert.Equal(98.00m, delta);
        Assert.Null(PaymentSettlementAdjustment.CheckDocumentLines(delta, 37.00m, 135.00m, 536.00m, 438.00m));

        // JE ของใบ: Dr ค่าใช้จ่าย 500.93 · Dr ภาษีซื้อ 35.07 (เคลมเต็มตามใบ — ข้อ 2 คงเดิม) · Cr เงินสด 536 (ตามยอดเอกสาร)
        // + adjusting lines (Dr 51120 37 · Cr 51150 135) + ขาเงินสดตามส่วนต่าง (Dr เงินสด 98) ⇒ เงินสดสุทธิ Cr 438
        var (cashBackDr, cashBackCr) = PaymentSettlementAdjustment.JournalSide(delta);
        var dr = 500.93m + 35.07m + 37.00m + cashBackDr;
        var cr = 536.00m + 135.00m + cashBackCr;
        Assert.Equal(dr, cr);
        Assert.Equal(438.00m, 536.00m - cashBackDr + cashBackCr);
    }

    [Fact]
    public void ใบสำคัญจ่ายระบุยอดชำระจริงแต่ไม่มีบรรทัดปรับ_หยุดพร้อมทางไปต่อ()
    {
        var msg = PaymentSettlementAdjustment.CheckDocumentLines(98.00m, 0m, 0m, 536.00m, 438.00m);
        Assert.NotNull(msg);
        Assert.Contains("438.00", msg);
        Assert.Contains("536.00", msg);
        Assert.Contains(PaymentSettlementAdjustment.FreightInAccountCode, msg);
        Assert.Contains(PaymentSettlementAdjustment.PurchaseDiscountAccountCode, msg);
        Assert.Contains("ยอดชำระจริง", msg);   // ทางไปต่อ: ล้างช่องถ้าจ่ายเต็ม
    }

    [Fact]
    public void บรรทัดปรับอธิบายส่วนต่างได้ไม่ครบ_หยุด_บอกว่าขาดเท่าไร()
    {
        var msg = PaymentSettlementAdjustment.CheckDocumentLines(98.00m, 0m, 135.00m, 536.00m, 438.00m);
        Assert.NotNull(msg);
        Assert.Contains("37.00", msg);   // ขาดค่าส่ง
    }

    [Fact]
    public void ปิดหนี้เกินยอดค้าง_ถูกปฏิเสธ()
    {
        var c = PaymentSettlementAdjustment.Check(500.00m, 536.00m, new[]
        {
            new SettlementAdjustmentLine("51150", -135.00m, "คูปอง"),
        });
        Assert.False(c.Ok);
        Assert.Contains("635.00", c.Error);
    }

    [Theory]
    [InlineData("", 37.0)]
    [InlineData("51120", 0.0)]
    [InlineData("51120", 37.005)]
    public void บรรทัดปรับที่ไม่ครบ_ถูกปฏิเสธ(string code, double amount)
    {
        var c = PaymentSettlementAdjustment.Check(438.00m, 536.00m, new[]
        {
            new SettlementAdjustmentLine(code, (decimal)amount, null),
        });
        Assert.False(c.Ok);
        Assert.False(string.IsNullOrWhiteSpace(c.Error));
    }

    [Fact]
    public void บรรทัดปรับกลับเครื่องหมายจนไม่ได้ปิดหนี้เลย_ถูกปฏิเสธ()
    {
        var c = PaymentSettlementAdjustment.Check(100.00m, 536.00m, new[] { new SettlementAdjustmentLine("51120", 150.00m, "ค่าส่ง") });
        Assert.False(c.Ok);
    }

    [Fact]
    public void ผังที่ใช้ลงส่วนต่าง_ความหมายตรงผังมาตรฐาน()
    {
        // เลขผังที่ hardcode ต้องชี้ความหมายเดียวกับผังมาตรฐาน (gl_code_check) — 51120 ค่าขนส่งเข้า · 51150 ส่วนลดรับ
        var chart = Accounting.Services.ChartOfAccountTemplates.GetCommonAccounts();
        Assert.Contains("ขนส่ง", chart.Single(a => a.Code == PaymentSettlementAdjustment.FreightInAccountCode).NameTh);
        Assert.Contains("ส่วนลดรับ", chart.Single(a => a.Code == PaymentSettlementAdjustment.PurchaseDiscountAccountCode).NameTh);
    }

    // ── ครึ่งที่ 2 (ห้ามแตะ) ────────────────────────────────────────────────

    [Fact]
    public void ไม่มีบรรทัดปรับ_ยอดที่ปิดเท่าเงินที่จ่าย_พฤติกรรมเดิม()
    {
        var none = PaymentSettlementAdjustment.Check(1000.00m, 536.00m, null);
        Assert.True(none.Ok);
        Assert.Equal(1000.00m, none.SettledAmount);   // ด่านจ่ายเกินเดิมเป็นคนตัดสิน ไม่ใช่ตัวนี้
        Assert.Equal(0m, none.AdjustmentNet);
        var empty = PaymentSettlementAdjustment.Check(438.00m, 536.00m, Array.Empty<SettlementAdjustmentLine>());
        Assert.True(empty.Ok);
        Assert.Equal(0m, empty.AdjustmentNet);
    }

    [Theory]
    [InlineData(DocumentType.PaymentVoucher, PaymentType.Credit, false)]   // PV เงินเชื่อ (legacy) ลงเจ้าหนี้ ไม่ใช่เงินสด
    [InlineData(DocumentType.PurchaseInvoice, null, false)]                  // ตั้งหนี้ — ส่วนต่างอยู่ที่การชำระ
    [InlineData(DocumentType.Expense, PaymentType.Credit, false)]
    [InlineData(DocumentType.PaymentVoucher, PaymentType.Cash, true)]        // ภ.พ.36 ขาเงินสดเป็นฐานเท่านั้น
    public void เอกสารที่ไม่จ่ายเงินในตัว_ส่วนต่างขาเงินสดเป็นศูนย์(DocumentType type, PaymentType? pt, bool foreign)
        => Assert.Equal(0m, PaymentSettlementAdjustment.DocumentCashDelta(type, pt, foreign, 536.00m, 438.00m));

    [Fact]
    public void ไม่ได้ระบุยอดชำระจริง_ส่วนต่างศูนย์_adjustingต้องnetzeroตามเดิม()
    {
        Assert.Equal(0m, PaymentSettlementAdjustment.DocumentCashDelta(
            DocumentType.PaymentVoucher, PaymentType.Cash, false, 536.00m, null));
        Assert.Null(PaymentSettlementAdjustment.CheckDocumentLines(0m, 50.00m, 50.00m, 536.00m, null));
        var legacy = PaymentSettlementAdjustment.CheckDocumentLines(0m, 50.00m, 40.00m, 536.00m, null);
        Assert.NotNull(legacy);
        Assert.Contains("ไม่ balance", legacy);   // ข้อความเดิมของ AutoPost
    }
}
