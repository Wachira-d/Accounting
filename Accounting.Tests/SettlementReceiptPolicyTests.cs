using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กติกาของใบเสร็จที่ออกคู่กับการรับชำระ — ตัวเดียวของทั้งเส้น "บันทึกรับชำระ"
/// และเส้น "ออกใบย้อนหลัง"
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-09-11) ═══
/// หลังปิดช่องที่ใบกำกับยกหัวเป็นใบเสร็จผิดวัน (ม.105) ใบ <c>TIV-20260805-0005</c>
/// กลับเป็น "ใบกำกับภาษี" ถูกต้อง — แต่การรับชำระ <c>PAY-202609-0010</c> ที่บันทึกไว้
/// **ก่อน**การแก้ ยังเหลือ JE รับเงิน (<c>RV-202609-0006</c>) โดย<b>ไม่มีเอกสารใบรับ
/// ให้ลูกค้าเลย</b> ⇒ ต้องมีทางออกใบย้อนหลัง และทางนั้นต้องใช้กติกาชุดเดียวกับ
/// ตอนบันทึกรับชำระ ไม่ใช่สำเนามือชุดที่สอง
/// </summary>
public class SettlementReceiptPolicyTests
{
    // ── ใบเสร็จถือ VAT (= ใบกำกับ ณ วันรับเงิน §78/1) หรือไม่ ──
    [Fact]
    public void ใบแจ้งหนี้มีVATปิดยอดงวดเดียว_ใบเสร็จคือใบกำกับณวันรับเงิน()
        => Assert.True(SettlementReceiptPolicy.CarriesTaxInvoiceRole(
            DocumentType.Invoice, 700m, singleShotFull: true));

    [Fact]
    public void ใบกำกับภาษีต้นทาง_ใบเสร็จต้องไม่ถือVATซ้ำ()
        // VAT ออกไปแล้วที่ใบกำกับ — ถือซ้ำ = ภาษีขายเข้า ภ.พ.30 สองรอบ
        => Assert.False(SettlementReceiptPolicy.CarriesTaxInvoiceRole(
            DocumentType.TaxInvoice, 7_525m, singleShotFull: true));

    [Fact]
    public void ผ่อนหลายงวด_ใบเสร็จเป็นใบรับเปล่า()
        => Assert.False(SettlementReceiptPolicy.CarriesTaxInvoiceRole(
            DocumentType.Invoice, 700m, singleShotFull: false));

    [Fact]
    public void ใบแจ้งหนี้ไม่มีVAT_ไม่ถือบทบาทใบกำกับ()
        => Assert.False(SettlementReceiptPolicy.CarriesTaxInvoiceRole(
            DocumentType.Invoice, 0m, singleShotFull: true));

    // ── ด่าน "ออกใบย้อนหลังได้ไหม" ──
    [Fact]
    public void เคสจริงของผู้ใช้_ใบกำกับที่รับเงินแล้วไม่มีใบเสร็จ_ต้องออกได้()
    {
        // TIV-20260805-0005 · 111,800 · ชำระครบแล้ว (Paid) · รับชำระรายการเดียว
        Assert.Null(SettlementReceiptPolicy.WhyCannotIssue(
            paymentVoided: false, DocumentType.TaxInvoice, DocumentStatus.Paid,
            allocationCount: 0, sourceServesAsReceipt: false));
    }

    [Theory]
    [InlineData(DocumentStatus.Paid)]
    [InlineData(DocumentStatus.PartiallyPaid)]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Sent)]
    [InlineData(DocumentStatus.Overdue)]
    public void ทุกสถานะที่รับเงินได้จริง_ต้องออกใบเสร็จย้อนหลังได้(DocumentStatus st)
        // ด่านเขียนเป็น "ห้ามสถานะไหน" ไม่ใช่ "ต้องเป็นสถานะไหน" — สถานะเป็น
        // lifecycle ที่ไหลไปข้างหน้าเอง (บทเรียนเดิมของ e-Tax gate)
        => Assert.Null(SettlementReceiptPolicy.WhyCannotIssue(
            false, DocumentType.Invoice, st, 0, sourceServesAsReceipt: false));

    [Fact]
    public void การชำระที่ถูกยกเลิก_ห้ามออกใบ()
    {
        var why = SettlementReceiptPolicy.WhyCannotIssue(
            paymentVoided: true, DocumentType.TaxInvoice, DocumentStatus.Paid, 0,
            sourceServesAsReceipt: false);
        Assert.NotNull(why);
        Assert.Contains("ยกเลิก", why!);
    }

    [Fact]
    public void เงินก้อนเดียวกระจายหลายใบ_ห้ามเดายอด_และต้องบอกทางไปต่อ()
    {
        var why = SettlementReceiptPolicy.WhyCannotIssue(
            false, DocumentType.Invoice, DocumentStatus.Paid, allocationCount: 3,
            sourceServesAsReceipt: false);
        Assert.NotNull(why);
        Assert.Contains("แปลงเอกสาร", why!);   // ปฏิเสธแล้วต้องมีทางไปต่อ ไม่ใช่ตันเฉย ๆ
    }

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    [InlineData(DocumentType.PaymentVoucher)]
    [InlineData(DocumentType.Receipt)]
    public void ฝั่งซื้อและใบเสร็จเอง_ออกใบเสร็จรับเงินไม่ได้(DocumentType t)
    {
        Assert.False(SettlementReceiptPolicy.IsReceivableSource(t));
        Assert.NotNull(SettlementReceiptPolicy.WhyCannotIssue(
            false, t, DocumentStatus.Paid, 0, sourceServesAsReceipt: false));
    }

    [Fact]
    public void เอกสารต้นทางที่ถูกยกเลิกหรือยังเป็นร่าง_ห้ามออก()
    {
        Assert.NotNull(SettlementReceiptPolicy.WhyCannotIssue(
            false, DocumentType.TaxInvoice, DocumentStatus.Voided, 0, sourceServesAsReceipt: false));
        Assert.NotNull(SettlementReceiptPolicy.WhyCannotIssue(
            false, DocumentType.TaxInvoice, DocumentStatus.Draft, 0, sourceServesAsReceipt: false));
    }

    [Fact]
    public void เคสจริงของผู้ใช้_ใบที่หัวเป็นใบกำกับใบเสร็จอยู่แล้ว_ห้ามออกใบเสร็จซ้ำ()
    {
        // TIV-20260805-0007 · ใบลงวันที่ 5 ส.ค. · PAY-202608-0007 รับเงิน 5 ส.ค.
        // (วันเดียวกัน) ⇒ หัวกระดาษเป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน" อยู่แล้ว
        // แต่หน้าจอยังโชว์ปุ่ม "ออกใบเสร็จ" ⇒ กดแล้วได้ใบรับใบที่สองของเงินก้อนเดิม
        var why = SettlementReceiptPolicy.WhyCannotIssue(
            false, DocumentType.TaxInvoice, DocumentStatus.Paid, 0,
            sourceServesAsReceipt: true);
        Assert.NotNull(why);
        Assert.Contains("ใบเสร็จของการรับเงินนี้แล้ว", why!);
        // ปฏิเสธแล้วต้องบอกทางไปต่อ (ตั้งค่าโหมดแยกใบ) ไม่ใช่ตันเฉย ๆ
        Assert.Contains("แยกใบกำกับภาษี", why!);
    }

    [Fact]
    public void ใบที่ยังไม่ได้ยกหัวเป็นใบเสร็จ_ยังออกใบเสร็จย้อนหลังได้เหมือนเดิม()
        // ทิศตรงข้ามของเคสบน — ด่านใหม่ต้องไม่ปิดเส้นที่เพิ่งเปิดให้ผู้ใช้
        // (รับเงินคนละวันกับวันที่ใบ = ใบต้นทางไม่ใช่ใบเสร็จ ⇒ ต้องออกได้)
        => Assert.Null(SettlementReceiptPolicy.WhyCannotIssue(
            false, DocumentType.TaxInvoice, DocumentStatus.Paid, 0,
            sourceServesAsReceipt: false));

    [Fact]
    public void ทุกการปฏิเสธต้องมีข้อความไทยที่เอาไปโชว์ได้()
    {
        // ห้ามคืน bool เปล่า ๆ แล้วให้แต่ละหน้าจอแต่งคำเอง (= สำเนาชุดที่สาม)
        var reasons = new[]
        {
            SettlementReceiptPolicy.WhyCannotIssue(true, DocumentType.Invoice, DocumentStatus.Paid, 0, false),
            SettlementReceiptPolicy.WhyCannotIssue(false, DocumentType.Expense, DocumentStatus.Paid, 0, false),
            SettlementReceiptPolicy.WhyCannotIssue(false, DocumentType.Invoice, DocumentStatus.Voided, 0, false),
            SettlementReceiptPolicy.WhyCannotIssue(false, DocumentType.Invoice, DocumentStatus.Paid, 2, false),
            SettlementReceiptPolicy.WhyCannotIssue(false, DocumentType.TaxInvoice, DocumentStatus.Paid, 0, true),
        };
        Assert.All(reasons, r => Assert.False(string.IsNullOrWhiteSpace(r)));
        Assert.Equal(5, reasons.Distinct().Count());
    }
}
