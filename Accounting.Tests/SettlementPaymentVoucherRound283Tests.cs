using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Pdf;
using Xunit;
using A = Accounting.Helpers.SettlementPaymentVoucher.IssueAction;

namespace Accounting.Tests;

/// <summary>รอบ 283 (คำสั่งเจ้าของ · 283-1…283-6): ตัวตัดสินใบสำคัญจ่ายหลักฐานคู่การจ่าย <see cref="SettlementPaymentVoucher"/> + ข้อความบนใบ (DocumentLabels th/en)</summary>
public class SettlementPaymentVoucherRound283Tests
{
    private static SettlementPaymentVoucher.Facts F(bool found = true, bool deleted = false, decimal amount = 10_400m, DocumentType type = DocumentType.PurchaseInvoice,
        DocumentStatus status = DocumentStatus.Paid, bool srcDeleted = false, Guid? existing = null, bool converted = false)
        => new(found, deleted, amount, type, status, srcDeleted, existing, converted);

    [Fact]
    public void การจ่ายใบซื้อปกติ_ออกใบ() => Assert.Equal(A.Issue, SettlementPaymentVoucher.Decide(F()).Action);

    [Fact]
    public void ใบบันทึกค่าใช้จ่าย_ออกได้() => Assert.Equal(A.Issue, SettlementPaymentVoucher.Decide(F(type: DocumentType.Expense, status: DocumentStatus.PartiallyPaid)).Action);

    [Fact]
    public void มีใบหลักฐานของการจ่ายนี้แล้ว_ไม่ออกซ้ำ_คืนใบเดิม()
    {
        var id = Guid.NewGuid();
        var d = SettlementPaymentVoucher.Decide(F(existing: id));
        Assert.Equal(A.AlreadyIssued, d.Action);
        Assert.Equal(id, d.ExistingVoucherId);
    }

    [Theory]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.PaymentVoucher)]
    public void ไม่ใช่ใบตั้งหนี้ฝั่งซื้อ_ไม่ออก(DocumentType t) => Assert.Equal(A.NotApplicable, SettlementPaymentVoucher.Decide(F(type: t)).Action);

    [Fact]
    public void การจ่ายถูกยกเลิก_หรือไม่พบ_ไม่ออก()
    {
        Assert.Equal(A.NotApplicable, SettlementPaymentVoucher.Decide(F(deleted: true)).Action);
        Assert.Equal(A.NotApplicable, SettlementPaymentVoucher.Decide(F(found: false)).Action);
    }

    [Theory]
    [InlineData(DocumentStatus.Voided, false)]
    [InlineData(DocumentStatus.Rejected, false)]
    [InlineData(DocumentStatus.Paid, true)]
    public void ใบตั้งหนี้ยกเลิก_หรือลบ_ไม่ออก(DocumentStatus status, bool srcDeleted)
        => Assert.Equal(A.NotApplicable, SettlementPaymentVoucher.Decide(F(status: status, srcDeleted: srcDeleted)).Action);

    [Fact]
    public void มีใบสำคัญจ่ายแบบแปลงที่ลงบัญชีเองแล้ว_ไม่ออกหลักฐานซ้อน()
        => Assert.Equal(A.NotApplicable, SettlementPaymentVoucher.Decide(F(converted: true)).Action);

    [Fact]
    public void ปิดด้วยบรรทัดปรับอย่างเดียว_ไม่มีเงินออก_ไม่ออก()
        => Assert.Equal(A.NotApplicable, SettlementPaymentVoucher.Decide(F(amount: 0m)).Action);

    [Fact]
    public void ใบหลักฐานไม่ถือ_VAT_และภาษีหัก()
    {
        Assert.Equal(0m, SettlementPaymentVoucher.VatAmount);
        Assert.Equal(0m, SettlementPaymentVoucher.WithholdingTaxAmount);
        Assert.Equal(10_400.01m, SettlementPaymentVoucher.SubTotal(10_400.005m));   // AwayFromZero
    }

    [Fact]
    public void ป้ายที่มา_เฉพาะใบจากท่อคู่ค้า()
    {
        Assert.Equal(SettlementPaymentVoucher.PartnerOriginLabel, SettlementPaymentVoucher.OriginLabel(SettlementPaymentVoucher.PartnerOrigin));
        Assert.Null(SettlementPaymentVoucher.OriginLabel(null));
        Assert.Null(SettlementPaymentVoucher.OriginLabel("Lodging"));
    }

    [Fact]
    public void บรรทัดบนใบ_ไทย_มีภาษีหัก_และเลข50ทวิ()
    {
        var th = DocumentLabels.For(null).SettlementPaymentVoucherLine("PI-001", 10_400m, 300m, "WHT-2026-0001");
        Assert.Contains("PI-001", th);
        Assert.Contains("10,400.00", th);
        Assert.Contains("300.00", th);
        Assert.Contains("10,700.00", th);
        Assert.Contains("WHT-2026-0001", th);
        var noWht = DocumentLabels.For(null).SettlementPaymentVoucherLine("PI-002", 5_000m, 0m, null);
        Assert.DoesNotContain("หักภาษี", noWht);
    }

    [Fact]
    public void บรรทัดบนใบ_อังกฤษ_ไม่ปนไทย()
    {
        var en = DocumentLabels.For("en").SettlementPaymentVoucherLine("PI-001", 10_400m, 300m, null);
        Assert.DoesNotMatch("[฀-๿]", en);
        Assert.DoesNotMatch("[฀-๿]", DocumentLabels.For("en").SettlementPaymentVoucherNote("PI-001", new DateTime(2026, 11, 21)));
    }
}
