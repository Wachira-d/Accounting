using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 260-5 ทีมตัดสินใจ (erp-review/2026-09-29/DECISIONS.md 260-5-*) — ตัวตัดสิน pure ทั้งสองทิศ
/// <para>260-5-1: ใบเสร็จ/ใบกำกับ ณ วันรับเงิน ลงวันที่รับเงินจริงแม้ก่อนวันที่ใบแจ้งหนี้ (ม.105 · ม.78/1 · ม.86) · งวด VAT = เดือนรับเงิน ·
/// ใบที่ถือ VAT ได้หมายเหตุ [LATE-TAX-INVOICE] · ทิศตรงข้าม: รับเงินไม่ก่อนใบ / ใบเสร็จเปล่า / VAT 0 = ไม่มีหมายเหตุ</para>
/// <para>260-5-2: คืนค่าสแกน API v1 — ตัวตัดสินสแกนตัวเดียวกับเส้นเว็บ (OcrQuotaRefundRule) · สแกนสำเร็จจริงไม่ถูกแตะ · เข้าใบแล้ว = รอใบลดหนี้ ไม่อ้างว่าคืน</para>
/// </summary>
public class DecisionTeamRound2605Tests
{
    private const string Uninvoiced = nameof(UnappliedReason.UninvoicedSupply);
    private static readonly DateTime Received = new(2026, 10, 28);
    private static readonly DateTime InvoiceDate = new(2026, 11, 5);

    // ── 260-5-1 ──

    [Fact]
    public void P2605_1_ใบกำกับณวันรับเงิน_รับเงินก่อนใบแจ้งหนี้_ลงวันที่รับเงิน_มีหมายเหตุออกช้า()
    {
        // วันที่ใบเสร็จของการจัดสรร = วันรับเงิน (ไม่เลื่อนเป็นวันที่ใบแจ้งหนี้)
        Assert.Equal(Received, LateReceiptTaxPoint.EffectiveReceiptDate(Uninvoiced, Received, InvoiceDate));
        // งวดภาษี = เดือนรับเงิน (ยังยื่นทัน)
        var d = LateReceiptTaxPoint.Decide(Uninvoiced, Received, InvoiceDate, receiptPeriodLocked: false, today: new DateTime(2026, 11, 6));
        Assert.Equal(Received, d.TaxPointDate);
        Assert.False(d.LateFlag);

        var note = LateReceiptTaxPoint.IssuedAfterMoneyNote(Received, InvoiceDate, carriesTaxInvoiceRole: true, vatAmount: 350m);
        Assert.NotNull(note);
        Assert.StartsWith(LateReceiptTaxPoint.IssuedAfterMoneyMarker, note);
        Assert.Contains("28/10/2026", note);
        Assert.Contains("05/11/2026", note);
        Assert.Contains("ม.105", note);
        Assert.Contains("ม.86", note);
    }

    [Fact]
    public void P2605_1_ทิศตรงข้าม_รับเงินไม่ก่อนใบ_ใบเสร็จเปล่า_VATศูนย์_ไม่มีหมายเหตุ()
    {
        Assert.Null(LateReceiptTaxPoint.IssuedAfterMoneyNote(InvoiceDate, InvoiceDate, true, 350m));              // วันเดียวกัน
        Assert.Null(LateReceiptTaxPoint.IssuedAfterMoneyNote(InvoiceDate.AddDays(3), InvoiceDate, true, 350m));   // รับหลังใบ (ปกติ)
        Assert.Null(LateReceiptTaxPoint.IssuedAfterMoneyNote(Received, InvoiceDate, false, 350m));                // ใบเสร็จเปล่า — VAT อยู่ที่ใบกำกับต้นทาง
        Assert.Null(LateReceiptTaxPoint.IssuedAfterMoneyNote(Received, InvoiceDate, true, 0m));                   // ไม่มี VAT
        // เหตุผลอื่น (โอนเกิน/ยังไม่ทราบ) วันที่ = วันที่จัดสรร ตามคำตัดสิน 258-1-5 H1 (ไม่ขยับ)
        Assert.Equal(InvoiceDate, LateReceiptTaxPoint.EffectiveReceiptDate(nameof(UnappliedReason.Unknown), Received, InvoiceDate));
    }

    // ── 260-5-2 ──

    [Theory]
    [InlineData("Failed", "Azure", 3.0, false, false, false, OcrFailedScanRefundAction.RemoveUnbilled)]
    [InlineData("Failed", null, 0.0, false, false, false, OcrFailedScanRefundAction.RemoveUnbilled)]          // โควตาฟรีที่ถูกกิน — คืนช่อง
    [InlineData("Completed", OcrDuplicateReusePolicy.CachedEngine, 3.0, false, false, false, OcrFailedScanRefundAction.RemoveUnbilled)]
    [InlineData("Completed", OcrDuplicateReusePolicy.EtaxXmlEngine, 3.0, false, false, false, OcrFailedScanRefundAction.RemoveUnbilled)]
    [InlineData("Failed", "Azure", 5.0, false, false, true, OcrFailedScanRefundAction.CreditPrepaidAndRemove)]
    [InlineData("Failed", "Azure", 5.0, false, true, true, OcrFailedScanRefundAction.CreditPrepaid)]
    [InlineData("Failed", "Azure", 3.0, false, true, false, OcrFailedScanRefundAction.NeedsCreditNote)]
    [InlineData("Failed", "Azure", 0.0, false, true, false, OcrFailedScanRefundAction.None)]                 // เข้าใบแล้วแต่ไม่ได้คิดเงิน
    [InlineData("Failed", "Azure", 3.0, true, false, false, OcrFailedScanRefundAction.None)]                 // sandbox
    [InlineData("Completed", "Azure", 3.0, false, false, false, OcrFailedScanRefundAction.None)]             // ทิศตรงข้าม: ใช้เครื่องอ่านจริง
    [InlineData("Completed", null, 3.0, false, false, true, OcrFailedScanRefundAction.None)]                 // engine ไม่ทราบ = ถือว่าใช้จริง
    public void P2605_2_ตัดสินทางคืนต่อแถว(string status, string? engine, double charged, bool sandbox, bool billed, bool prepaid, OcrFailedScanRefundAction expected)
        => Assert.Equal(expected, OcrFailedScanRefund.Decide(true, status, engine, (decimal)charged, sandbox, billed, prepaid));

    [Fact]
    public void P2605_2_ไม่พบสแกน_ไม่เดา()
        => Assert.Equal(OcrFailedScanRefundAction.None, OcrFailedScanRefund.Decide(false, null, null, 3m, false, false, false));
}
