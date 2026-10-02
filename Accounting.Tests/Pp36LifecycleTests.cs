using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 203 ทีม F3 — วงจรนำส่ง/รับรู้ ภ.พ.36 (PP36_REVIEW E-2..E-10 · คำตัดสินข้อ 129, 133–136) · ตัวตัดสิน pure
/// (<see cref="Pp36Lifecycle"/> · <see cref="ForeignServiceVat.OwnsPp36"/> · <see cref="DocumentFx"/> · <see cref="Pp36DocRow.CountedVat"/>)
/// ทุกข้อมีสองทิศ: ใบ/กรณีที่ต้องถูกนับ/ผ่าน กับใบที่ต้องไม่ถูกนับ/ถูกปฏิเสธ
/// </summary>
public class Pp36LifecycleTests
{
    private static readonly Pp36LedgerFacts WithJe = new(413.56m, 413.56m);

    // ── สถานะของใบ (ตัวเดียวของยอดค้าง/นำส่ง/รับรู้/รายงาน/ป้าย) ──

    [Fact]
    public void ใบมีJE_ยังไม่นำส่ง_รอนำส่ง()
        => Assert.Equal(Pp36DocState.AwaitingRemittance, Pp36Lifecycle.Classify(true, true, WithJe, false, false));

    [Fact]
    public void ใบอนุมัติแล้วไม่มีJE_ต้องซ่อม_ไม่ใช่รอนำส่ง()
        => Assert.Equal(Pp36DocState.NoJournal, Pp36Lifecycle.Classify(true, true, default, false, false));

    [Fact]
    public void นำส่งแล้ว_มีภาษีซื้อพัก_รอรับรู้()
        => Assert.Equal(Pp36DocState.RemittedAwaitingRecognition, Pp36Lifecycle.Classify(true, true, WithJe, true, false));

    [Fact]
    public void นำส่งแล้ว_บริษัทไม่จดVAT_ไม่มีภาษีซื้อให้รับรู้()
        => Assert.Equal(Pp36DocState.RemittedNoInputVat,
            Pp36Lifecycle.Classify(true, true, new Pp36LedgerFacts(413.56m, 0m), true, false));

    [Fact]
    public void รับรู้แล้ว_ชนะทุกสถานะ()
        => Assert.Equal(Pp36DocState.Recognized, Pp36Lifecycle.Classify(true, true, WithJe, true, true));

    [Fact]
    public void นำส่งแล้วแต่ไม่มีJE_ข้อมูลเก่า_ไม่ถูกเชิญให้นำส่งซ้ำ()
        => Assert.Equal(Pp36DocState.RemittedNoInputVat, Pp36Lifecycle.Classify(true, true, default, true, false));

    [Theory]
    [InlineData(false, true)]   // ไม่ใช่เจ้าของ (PV ปิดหนี้ใบต้นทาง)
    [InlineData(true, false)]   // ยกเลิก/ยังไม่ออก
    public void ไม่ใช่เจ้าของหรือไม่มีผล_ไม่เกี่ยวกับภพ36(bool owns, bool effective)
        => Assert.Equal(Pp36DocState.NotApplicable, Pp36Lifecycle.Classify(owns, effective, WithJe, true, true));

    // ── ยอดที่นับ (บาท · ใบไม่มี JE = 0) ──

    private static Pp36DocRow Row(Pp36DocState state, Pp36LedgerFacts ledger, decimal? linked = null) => new(
        Guid.NewGuid(), "PV-1", DocumentType.PaymentVoucher, new DateTime(2026, 9, 15), Guid.NewGuid(), true, ledger,
        linked is decimal v ? new Pp36RemittanceDocument { VatAmount = v } : null, null, state);

    [Fact]
    public void ยอดที่นับ_รอนำส่งใช้GL_นำส่งแล้วใช้ยอดที่นำส่ง_ไม่มีJEเป็นศูนย์()
    {
        Assert.Equal(413.56m, Row(Pp36DocState.AwaitingRemittance, WithJe).CountedVat);
        Assert.Equal(400m, Row(Pp36DocState.RemittedAwaitingRecognition, WithJe, 400m).CountedVat);
        Assert.Equal(400m, Row(Pp36DocState.Recognized, WithJe, 400m).CountedVat);
        Assert.Equal(0m, Row(Pp36DocState.NoJournal, default).CountedVat);
        Assert.Equal(0m, Row(Pp36DocState.NotApplicable, WithJe).CountedVat);
    }

    // ── เจ้าของหนี้ ภ.พ.36 + ชุดชนิดเดียว (E-8 · E-1b) ──

    [Theory]
    [InlineData(DocumentType.PaymentVoucher, true)]
    [InlineData(DocumentType.PurchaseInvoice, true)]
    [InlineData(DocumentType.Expense, true)]
    [InlineData(DocumentType.CertificateInLieu, false)]   // AutoPost ไม่แยกขา §83/6 ให้ CIL (ไม่มี Cr 21912) ⇒ ติ๊กไม่ได้ · ไม่ใช่เจ้าของ
    [InlineData(DocumentType.TaxInvoice, false)]
    [InlineData(DocumentType.Invoice, false)]
    [InlineData(DocumentType.Receipt, false)]
    public void ชุดชนิดเดียว_ด่านติ๊กธงกับเจ้าของหนี้ตรงกัน(DocumentType type, bool expected)
    {
        Assert.Equal(expected, ForeignServiceVat.IsSelfAssessingType(type));
        Assert.Equal(expected, Pp36Lifecycle.FlagTypeError(type, true) == null);
        Assert.Equal(expected, ForeignServiceVat.OwnsPp36(type, true, 413.56m, false));
        Assert.False(ForeignServiceVat.OwnsPp36(type, false, 413.56m, false));
    }

    [Fact]
    public void ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทาง_ไม่ใช่เจ้าของ_ใบตั้งหนี้เป็นเจ้าของ()
    {
        Assert.False(ForeignServiceVat.OwnsPp36(DocumentType.PaymentVoucher, true, 413.56m, hasRelatedDocument: true));
        Assert.True(ForeignServiceVat.OwnsPp36(DocumentType.PaymentVoucher, true, 413.56m, hasRelatedDocument: false));
        Assert.True(ForeignServiceVat.OwnsPp36(DocumentType.PurchaseInvoice, true, 413.56m, hasRelatedDocument: true));
        Assert.False(ForeignServiceVat.OwnsPp36(DocumentType.PurchaseInvoice, true, 0m, hasRelatedDocument: false));   // ไม่มี VAT = ไม่มีหนี้
    }

    [Fact]
    public void งวดภพ36_วันจ่ายก่อน_ไม่มีวันจ่ายใช้วันที่เอกสาร()
    {
        Assert.Equal(new DateTime(2026, 10, 2), ForeignServiceVat.Pp36PeriodDate(new DateTime(2026, 10, 2), new DateTime(2026, 9, 30)));
        Assert.Equal(new DateTime(2026, 9, 30), ForeignServiceVat.Pp36PeriodDate(null, new DateTime(2026, 9, 30)));
    }

    [Fact]
    public void ติ๊กธงบนใบขายหรือCIL_ถูกปฏิเสธ_ใบซื้อผ่าน_ไม่ติ๊กผ่านทุกชนิด()
    {
        Assert.NotNull(Pp36Lifecycle.FlagTypeError(DocumentType.TaxInvoice, true));
        Assert.NotNull(Pp36Lifecycle.FlagTypeError(DocumentType.CertificateInLieu, true));
        Assert.Null(Pp36Lifecycle.FlagTypeError(DocumentType.PaymentVoucher, true));
        Assert.Null(Pp36Lifecycle.FlagTypeError(DocumentType.TaxInvoice, false));
    }

    // ── วันเคลม = วันใบเสร็จ RD (คำตัดสินข้อ 129) ──

    [Fact]
    public void วันเคลมค่าเริ่มต้น_วันที่ใบเสร็จ()
    {
        var (date, err) = Pp36Lifecycle.ResolveClaimDate(new DateTime(2026, 10, 5, 13, 0, 0), null);
        Assert.Null(err);
        Assert.Equal(new DateTime(2026, 10, 5), date);
    }

    [Fact]
    public void วันเคลมก่อนใบเสร็จ_ถูกปฏิเสธพร้อมทางไปต่อ()
    {
        var (date, err) = Pp36Lifecycle.ResolveClaimDate(new DateTime(2026, 10, 5), new DateTime(2026, 9, 15));
        Assert.Null(date);
        Assert.Contains("ก่อนวันที่ใบเสร็จ", err);
        Assert.Contains("เว้นว่าง", err);
    }

    [Fact]
    public void วันเคลมหลังใบเสร็จ_ผู้ใช้เลือกได้()
    {
        var (date, err) = Pp36Lifecycle.ResolveClaimDate(new DateTime(2026, 10, 5), new DateTime(2026, 11, 1));
        Assert.Null(err);
        Assert.Equal(new DateTime(2026, 11, 1), date);
    }

    // ── เงินเพิ่ม §89/1 (คำตัดสินข้อ 135) ──

    [Fact]
    public void เงินเพิ่ม_ทันกำหนดศูนย์_เลยวันเดียวคิดหนึ่งเดือน_เศษเดือนนับเป็นเดือน()
    {
        var due = new DateTime(2026, 10, 7);
        Assert.Equal(0m, Pp36Lifecycle.SuggestedSurcharge(413.56m, due, due));
        Assert.Equal(6.20m, Pp36Lifecycle.SuggestedSurcharge(413.56m, due, due.AddDays(1)));     // 413.56 × 1.5% = 6.2034
        Assert.Equal(12.41m, Pp36Lifecycle.SuggestedSurcharge(413.56m, due, new DateTime(2026, 11, 8)));   // 2 เดือน 12.4068
    }

    [Fact]
    public void เงินเพิ่ม_ไม่เกินจำนวนภาษี()
        => Assert.Equal(413.56m, Pp36Lifecycle.SuggestedSurcharge(413.56m, new DateTime(2020, 1, 7), new DateTime(2026, 10, 2)));

    // ── ป้าย + ข้อความบล็อก ──

    [Fact]
    public void ป้ายรับรู้แล้วบอกเดือนเคลมเป็นพศ_ไม่เกี่ยวไม่มีป้าย()
    {
        Assert.Equal("ภ.พ.36 · รับรู้แล้ว เคลม ภ.พ.30 เดือน 10/2569", Pp36Lifecycle.Label(Pp36DocState.Recognized, new DateTime(2026, 10, 5)));
        Assert.Equal("ภ.พ.36 · ไม่มี JE ต้องซ่อม", Pp36Lifecycle.Label(Pp36DocState.NoJournal, null));
        Assert.Null(Pp36Lifecycle.Label(Pp36DocState.NotApplicable, null));
    }

    [Fact]
    public void ข้อความบล็อกหลังนำส่ง_บอกงวดและทางไปต่อ()
    {
        var st = new Pp36RemitStatus("PV-20260901-0001", 2026, 9, new DateTime(2026, 10, 5), Recognized: true, RdReceiptNumber: "RD-1");
        var m = Pp36Lifecycle.ChangeBlockMessage("PV-20260901-0001", "ยกเลิก", st);
        Assert.Contains("PV-20260901-0001", m);
        Assert.Contains("09/2569", m);
        Assert.Contains("รับรู้ภาษีซื้อ", m);
        Assert.Contains("ใบสำคัญทั่วไป", m);
        // เหตุสั้นที่ด่านใบลดหนี้ (ทีม F2) ใช้ — ตัวเดียวกัน ไม่มีทางไปต่อซ้อน
        var reason = Pp36Lifecycle.SettledReason(st with { Recognized = false, RdReceiptNumber = null });
        Assert.Contains("นำส่ง ภ.พ.36 แล้ว", reason);
        Assert.DoesNotContain("รับรู้", reason);
        Assert.DoesNotContain("ใบสำคัญทั่วไป", reason);
    }

    // ── ตัวแปลงบาทเดียวกับ JE (E-4) ──

    [Fact]
    public void แปลงบาท_USDอัตรา36_บาทอัตรา1ไม่แตะ_อัตราศูนย์ถือเป็น1()
    {
        Assert.Equal(2520m, DocumentFx.ToBaht(70m, 36m));
        Assert.Equal(413.56m, DocumentFx.ToBaht(413.56m, 1m));
        Assert.Equal(413.56m, DocumentFx.ToBaht(413.56m, 0m));
        Assert.Equal(0.02m, DocumentFx.ToBaht(0.005m, 3m));   // 0.015 → AwayFromZero 0.02
    }

    [Fact]
    public void ผังภาษีซื้อพัก_11640และ11630_ไม่ใช่11610()
    {
        Assert.True(Pp36Ledger.IsUndueCode("11640"));
        Assert.True(Pp36Ledger.IsUndueCode("11630"));
        Assert.False(Pp36Ledger.IsUndueCode("11610"));
    }
}
