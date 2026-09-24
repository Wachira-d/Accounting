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
            DocumentType.PaymentVoucher, PaymentType.Cash, isForeignService: false, settlesSourceDocument: false, 536.00m, 438.00m);
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
    [InlineData(DocumentType.PaymentVoucher, PaymentType.Credit, false)]   // PV เงินเชื่อเดี่ยว (legacy · ไม่มีใบต้นทาง) ลงเจ้าหนี้ ไม่ใช่เงินสด
    [InlineData(DocumentType.PurchaseInvoice, null, false)]                  // ตั้งหนี้ — ส่วนต่างอยู่ที่การชำระ
    [InlineData(DocumentType.Expense, PaymentType.Credit, false)]
    [InlineData(DocumentType.PaymentVoucher, PaymentType.Cash, true)]        // ภ.พ.36 ขาเงินสดเป็นฐานเท่านั้น
    public void เอกสารที่ไม่จ่ายเงินในตัว_ส่วนต่างขาเงินสดเป็นศูนย์(DocumentType type, PaymentType? pt, bool foreign)
        => Assert.Equal(0m, PaymentSettlementAdjustment.DocumentCashDelta(type, pt, foreign, settlesSourceDocument: false, 536.00m, 438.00m));

    // ── ฝ่ายค้าน C1 รอบ 193 ──────────────────────────────────────────────────
    // ใบสำคัญจ่ายที่แปลงจากใบตั้งหนี้ (RelatedDocumentId) ได้ PaymentType=Credit เป็นค่าเริ่มต้น แต่ JE สาขา settlement ลงเงินสด
    // เสมอ ⇒ ต้องนับเป็น "จ่ายในตัว" — เดิมตอบ false ⇒ ยอดชำระจริง 438 ถูกข้ามเงียบ JE ลงเงินสด 536 (เทสต์เดิมล็อกสมมติฐานผิดไว้)

    [Fact]
    public void ใบสำคัญจ่ายที่แปลงจากใบตั้งหนี้_แม้เป็นCredit_ลงเงินสดจริง_ส่วนต่าง98()
    {
        Assert.True(PaymentSettlementAdjustment.PostsCashAtApproval(
            DocumentType.PaymentVoucher, PaymentType.Credit, isForeignService: false, settlesSourceDocument: true));
        var delta = PaymentSettlementAdjustment.DocumentCashDelta(
            DocumentType.PaymentVoucher, PaymentType.Credit, false, settlesSourceDocument: true, 536.00m, 438.00m);
        Assert.Equal(98.00m, delta);
        // บรรทัดปรับ Dr 51120 37 / Cr 51150 135 ตามคำแนะนำ ⇒ ลงตัว (เดิมได้ "Adjusting JE Lines ไม่ balance")
        Assert.Null(PaymentSettlementAdjustment.CheckDocumentLines(delta, 37.00m, 135.00m, 536.00m, 438.00m));
        // JE settlement: Dr เจ้าหนี้ 536 · Cr เงินสด 536 + Dr เงินสด 98 (ส่วนต่าง) + Dr 51120 37 · Cr 51150 135 ⇒ เงินสดสุทธิ 438
        var (backDr, backCr) = PaymentSettlementAdjustment.JournalSide(delta);
        Assert.Equal(536.00m + backDr + 37.00m, 536.00m + backCr + 135.00m);
        Assert.Null(PaymentSettlementAdjustment.ActualPaidNotApplicableReason(
            DocumentType.PaymentVoucher, PaymentType.Credit, false, settlesSourceDocument: true));
    }

    [Theory]
    [InlineData(DocumentType.PaymentVoucher, PaymentType.Credit, false)]   // PV เงินเชื่อเดี่ยว — ไม่มีเงินออกตอนอนุมัติ
    [InlineData(DocumentType.PaymentVoucher, PaymentType.Cash, true)]      // ภ.พ.36
    [InlineData(DocumentType.TaxInvoice, null, false)]                     // ฝั่งขาย
    public void ช่องยอดชำระจริงที่ไม่มีผล_บอกผู้ใช้พร้อมทางไปต่อ_ไม่ข้ามเงียบ(DocumentType type, PaymentType? pt, bool foreign)
    {
        var why = PaymentSettlementAdjustment.ActualPaidNotApplicableReason(type, pt, foreign, settlesSourceDocument: false);
        Assert.NotNull(why);
        Assert.Contains("ยอดชำระจริง", why);
        Assert.Contains("ล้างช่อง", why);
    }

    [Fact]
    public void ใบตั้งหนี้แบบจ่ายทันที_ช่องยอดชำระจริงไม่มีผล_ต้องบอก()
    {
        // ฝ่ายค้านรอบสอง P-e: PI จ่ายทันทีปิดยอดตั้งแต่สร้าง ⇒ ไม่มีขั้นบันทึกการชำระให้ใช้ค่า ⇒ silent no-op
        var why = PaymentSettlementAdjustment.ActualPaidNotApplicableReason(
            DocumentType.PurchaseInvoice, PaymentType.Cash, false, settlesSourceDocument: false);
        Assert.NotNull(why);
        Assert.Contains("ใบสำคัญจ่าย", why);
    }

    [Theory]
    [InlineData("11111")]   // เงินสด
    [InlineData("11122")]   // เงินฝากออมทรัพย์
    [InlineData(" 11131")]  // เช็คในมือ (ช่องว่างนำหน้า)
    public void บรรทัดปรับใช้ผังเงินสดเงินฝากไม่ได้(string code)
    {
        // ฝ่ายค้านรอบสอง P-d: ผังเงินสด −98 กับเงิน 0 ⇒ GL เงินออก 98 แต่ Payment.Amount = 0 และยอดธนาคารไม่ขยับ
        Assert.True(PaymentSettlementAdjustment.IsMoneyAccountCode(code));
        var c = PaymentSettlementAdjustment.Check(0m, 98.00m, new[] { new SettlementAdjustmentLine(code, -98.00m, null) });
        Assert.False(c.Ok);
        Assert.Contains("จำนวนเงิน", c.Error);
    }

    [Theory]
    [InlineData("51120")]
    [InlineData("51150")]
    [InlineData("11200")]   // เงินลงทุนชั่วคราว — ไม่ใช่หมวดเงินสด
    public void ผังที่ไม่ใช่เงินสด_ไม่ถูกกัน(string code)
        => Assert.False(PaymentSettlementAdjustment.IsMoneyAccountCode(code));

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice, null)]    // ใช้เติมหน้าบันทึกการชำระ
    [InlineData(DocumentType.Expense, PaymentType.Credit)]
    [InlineData(DocumentType.PaymentVoucher, PaymentType.Cash)]
    public void ช่องยอดชำระจริงที่มีผล_ไม่ถูกปฏิเสธ(DocumentType type, PaymentType? pt)
        => Assert.Null(PaymentSettlementAdjustment.ActualPaidNotApplicableReason(type, pt, false, settlesSourceDocument: false));

    [Fact]
    public void ค่าเผื่อตัวเดียวกับด่านสมดุลของเอกสาร()
        => Assert.Equal(DocumentSettlementState.Tolerance, PaymentSettlementAdjustment.MatchTolerance);

    [Fact]
    public void ปิดหนี้ค้างด้วยบรรทัดปรับอย่างเดียว_เงินศูนย์_ผ่าน_และไม่เกินยอดค้าง()
    {
        // ฝ่ายค้าน C9: ชำระ 438 ผ่านทางอื่นแล้ว หนี้ค้าง 98 ⇒ ปิดด้วยคูปอง −98 โดยไม่มีเงินออก
        var ok = PaymentSettlementAdjustment.Check(0m, 98.00m, new[] { new SettlementAdjustmentLine("51150", -98.00m, "คูปอง") });
        Assert.True(ok.Ok);
        Assert.Equal(98.00m, ok.SettledAmount);
        Assert.Equal(98.00m, ok.AdjustmentNet);
        // ทิศตรงข้าม: ปิดเกินยอดค้าง / ไม่ได้ปิดอะไรเลย ⇒ ปฏิเสธ
        Assert.False(PaymentSettlementAdjustment.Check(0m, 98.00m, new[] { new SettlementAdjustmentLine("51150", -135.00m, null) }).Ok);
        Assert.False(PaymentSettlementAdjustment.Check(0m, 98.00m, new[] { new SettlementAdjustmentLine("51120", 37.00m, null) }).Ok);
    }

    [Fact]
    public void ไม่ได้ระบุยอดชำระจริง_ส่วนต่างศูนย์_adjustingต้องnetzeroตามเดิม()
    {
        Assert.Equal(0m, PaymentSettlementAdjustment.DocumentCashDelta(
            DocumentType.PaymentVoucher, PaymentType.Cash, false, false, 536.00m, null));
        Assert.Null(PaymentSettlementAdjustment.CheckDocumentLines(0m, 50.00m, 50.00m, 536.00m, null));
        var legacy = PaymentSettlementAdjustment.CheckDocumentLines(0m, 50.00m, 40.00m, 536.00m, null);
        Assert.NotNull(legacy);
        Assert.Contains("ไม่ balance", legacy);   // ข้อความเดิมของ AutoPost
    }
}
