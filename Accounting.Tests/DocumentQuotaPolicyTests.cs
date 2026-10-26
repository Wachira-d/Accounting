using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// โควตาเอกสาร — สองคำถามที่แยกกันเด็ดขาด: "นับไหม" กับ "บล็อกได้ไหม"
/// (LODGING_LICENSING_PLAN §5 · CLAUDE.md "ห้ามบล็อกเอกสารที่กฎหมายบังคับ")
///
/// เทสต์ชุดนี้ล็อก**ความหมายทางกฎหมาย** ไม่ใช่ค่าคงที่: ถ้าใครเผลอเปลี่ยน
/// ใบกำกับภาษีให้บล็อกได้ตอนโควตาเต็ม = ลูกค้าออกใบไม่ได้ทั้งที่ tax point
/// เกิดแล้ว (§86/4) แล้วเราเป็นสาเหตุที่ทำให้เขาผิดกฎหมาย
/// </summary>
public class DocumentQuotaPolicyTests
{
    [Theory]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.CreditNote)]
    [InlineData(DocumentType.DebitNote)]
    [InlineData(DocumentType.ReceiptVoucher)]
    [InlineData(DocumentType.CertificateInLieu)]
    public void เอกสารที่กฎหมายบังคับ_ห้ามบล็อกแม้โควตาเต็ม(DocumentType type)
    {
        var e = DocumentQuotaPolicy.Classify(type, isDeposit: false, fromLodging: false);
        Assert.False(DocumentQuotaPolicy.CanRefuseWhenOverQuota(e));
    }

    [Theory]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.PurchaseOrder)]
    [InlineData(DocumentType.PurchaseRequisition)]
    public void เอกสารที่รอได้_บล็อกได้เมื่อโควตาเต็ม(DocumentType type)
    {
        var e = DocumentQuotaPolicy.Classify(type, isDeposit: false, fromLodging: false);
        Assert.True(DocumentQuotaPolicy.CanRefuseWhenOverQuota(e));
    }

    [Theory]
    [InlineData(DocumentType.TaxInvoice, true)]
    [InlineData(DocumentType.Invoice, true)]
    [InlineData(DocumentType.Receipt, true)]
    // ใบลดหนี้/เพิ่มหนี้ไม่ใช่ "การขายใหม่" — นับซ้ำ = ลูกค้ารู้สึกโดนคิดสองเด้ง
    [InlineData(DocumentType.CreditNote, false)]
    [InlineData(DocumentType.DebitNote, false)]
    // เอกสารฝั่งซื้อเป็นของคู่ค้า เราแค่บันทึก (มีโควตา OCR แยกอยู่แล้ว)
    [InlineData(DocumentType.PurchaseInvoice, false)]
    [InlineData(DocumentType.Expense, false)]
    [InlineData(DocumentType.DeliveryNote, false)]
    public void นับโควตาเฉพาะใบที่แทนการขายหนึ่งครั้ง(DocumentType type, bool counts)
    {
        var e = DocumentQuotaPolicy.Classify(type, isDeposit: false, fromLodging: false);
        Assert.Equal(counts, DocumentQuotaPolicy.Counts(e));
    }

    [Theory]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.Receipt)]
    public void เอกสารจากโมดูลที่พักที่กฎหมายบังคับ_ยังนับโควตาเอกสาร_จนกว่าการแปลงstayเป็น2หน่วยจะมีโค้ด(DocumentType type)
    {
        // ฝ่ายค้าน ภ.ง.ด.1/Tb P1-1: 058233b5 เปลี่ยนเป็น "ไม่นับ" ตาม LODGING_LICENSING_PLAN §13.2 แต่ §13.2 แทนด้วย 1 stay = 2 หน่วยโควตา
        // ซึ่งยังไม่มีโค้ด (lodging.stay ราคา 0.0) ⇒ ปริมาณงานที่พักไม่ถูกคิดเงินที่ไหนเลย · คืนพฤติกรรมเดิม (ห้ามแย่ลง) — TODO ในตัวโค้ด
        var e = DocumentQuotaPolicy.Classify(type, isDeposit: false, fromLodging: true);
        Assert.Equal(QuotaEnforcement.CountedMustAllow, e);
        Assert.True(DocumentQuotaPolicy.Counts(e));
    }

    [Theory]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.DeliveryNote)]
    [InlineData(DocumentType.BillingNote)]
    public void ทิศตรงข้าม_เอกสารจากที่พักที่ไม่ใช่ใบบังคับ_ไม่นับและไม่มีวันถูกบล็อก(DocumentType type)
    {
        // ใบเสนอราคาทั่วไปบล็อกได้เมื่อเต็ม แต่ใบที่โมดูลที่พักออกเองห้ามทำให้เช็คอิน/เช็คเอาต์สะดุด
        var e = DocumentQuotaPolicy.Classify(type, isDeposit: false, fromLodging: true);
        Assert.Equal(QuotaEnforcement.NotCounted, e);
        Assert.False(DocumentQuotaPolicy.CanRefuseWhenOverQuota(e));
    }

    [Theory]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Receipt)]
    public void ทิศตรงข้าม_ใบชนิดเดียวกันที่ไม่ได้มาจากที่พัก_ยังนับและห้ามบล็อก(DocumentType type)
    {
        // การแก้ "ที่พักไม่นับ" ต้องไม่ลามไปใบขายทั่วไป (POS/ขายหน้าร้าน) ที่ยังคิดตามโควตาเอกสาร
        var e = DocumentQuotaPolicy.Classify(type, isDeposit: false, fromLodging: false);
        Assert.Equal(QuotaEnforcement.CountedMustAllow, e);
        Assert.True(DocumentQuotaPolicy.Counts(e));
        Assert.False(DocumentQuotaPolicy.CanRefuseWhenOverQuota(e));
    }

    [Fact]
    public void เอกสารจากที่พักที่กฎหมายบังคับ_ยังห้ามบล็อกเหมือนเดิม()
    {
        var e = DocumentQuotaPolicy.Classify(DocumentType.TaxInvoice, isDeposit: false, fromLodging: true);
        Assert.False(DocumentQuotaPolicy.CanRefuseWhenOverQuota(e));
    }

    // ───────── โบนัสโควตา ─────────

    [Fact]
    public void โบนัสที่ยังไม่หมดอายุ_บวกเข้าเพดาน()
    {
        var now = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(120, DocumentQuotaPolicy.EffectiveLimit(100, 20, now.AddDays(5), now));
    }

    [Fact]
    public void โบนัสหมดอายุแล้ว_ไม่บวก()
    {
        var now = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(100, DocumentQuotaPolicy.EffectiveLimit(100, 20, now.AddDays(-1), now));
    }

    [Fact]
    public void โบนัสไม่มีวันหมดอายุ_บวกได้()
    {
        var now = DateTime.UtcNow;
        Assert.Equal(150, DocumentQuotaPolicy.EffectiveLimit(100, 50, null, now));
    }

    /// <summary>เพดาน ≤ 0 = "ไม่จำกัด/ยังไม่ตั้งค่า" ไม่ใช่ "ศูนย์ใบ" —
    /// ถ้าเอาโบนัสไปบวก แพ็กเกจไม่จำกัดจะกลายเป็นจำกัดเท่าโบนัสทันที</summary>
    [Fact]
    public void เพดานไม่จำกัด_โบนัสต้องไม่ทำให้กลายเป็นจำกัด()
    {
        var now = DateTime.UtcNow;
        Assert.Equal(0, DocumentQuotaPolicy.EffectiveLimit(0, 500, null, now));
        Assert.Equal(-1, DocumentQuotaPolicy.EffectiveLimit(-1, 500, null, now));
    }

    // ───────── ระดับเตือน + พยากรณ์ ─────────

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(79, 100, 0)]
    [InlineData(80, 100, 1)]
    [InlineData(99, 100, 1)]
    [InlineData(100, 100, 2)]
    [InlineData(140, 100, 2)]
    public void ระดับเตือนตามสัดส่วนที่ใช้(int used, int limit, int expected)
        => Assert.Equal(expected, DocumentQuotaPolicy.WarnLevel(used, limit));

    [Fact]
    public void เพดานไม่จำกัด_ไม่เตือนอะไรเลย()
        => Assert.Equal(0, DocumentQuotaPolicy.WarnLevel(9999, 0));

    [Fact]
    public void พยากรณ์วันที่โควตาจะเต็มจากอัตราการใช้()
    {
        // ใช้ไป 50 ใบใน 10 วัน = 5 ใบ/วัน · เพดาน 100 ⇒ เต็มวันที่ 20
        Assert.Equal(20, DocumentQuotaPolicy.ForecastExhaustionDay(50, 100, 10, 30));
    }

    [Fact]
    public void ใช้ช้ากว่าโควตา_ไม่ต้องพยากรณ์()
    {
        // 10 ใบใน 10 วัน = 1 ใบ/วัน · เพดาน 100 ⇒ ไม่เต็มภายในเดือนนี้
        Assert.Null(DocumentQuotaPolicy.ForecastExhaustionDay(10, 100, 10, 30));
    }

    [Fact]
    public void ยังไม่ใช้เลย_พยากรณ์ไม่ได้_ต้องคืนไม่รู้ไม่ใช่เดา()
        => Assert.Null(DocumentQuotaPolicy.ForecastExhaustionDay(0, 100, 5, 30));

    // ── รอบ 205 ทีม W1 (R-03 · BOOKING_REVENUE_AUDIT Q-2): ข้อเท็จจริงใหม่ 3 ตัว + ทิศตรงข้าม ──

    [Theory]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.TaxInvoice)]
    public void R03_ใบมัดจำใบที่สองของการจองเดียวกัน_ไม่นับซ้ำ_และยังห้ามบล็อก(DocumentType type)
    {
        var e = DocumentQuotaPolicy.Classify(type, isDeposit: true, fromLodging: true, hasPriorDepositForBooking: true);
        Assert.False(DocumentQuotaPolicy.Counts(e));
        Assert.False(DocumentQuotaPolicy.CanRefuseWhenOverQuota(e));
    }

    [Fact]
    public void R03_ทิศตรงข้าม_ใบมัดจำใบแรก_ยังนับเหมือนเดิม()
    {
        var e = DocumentQuotaPolicy.Classify(DocumentType.Receipt, isDeposit: true, fromLodging: true, hasPriorDepositForBooking: false);
        Assert.Equal(QuotaEnforcement.CountedMustAllow, e);
    }

    [Fact]
    public void R03_ทิศตรงข้าม_ใบที่ไม่ใช่มัดจำ_ธงมีมัดจำก่อนหน้าไม่มีผล()
    {
        // ใบกำกับเช็คเอาต์ของการจองที่มีใบมัดจำแล้ว = หน่วยที่ 2 ของ "1 การเข้าพัก = 2 หน่วย" — ต้องนับ
        var e = DocumentQuotaPolicy.Classify(DocumentType.TaxInvoice, isDeposit: false, fromLodging: true, hasPriorDepositForBooking: true);
        Assert.Equal(QuotaEnforcement.CountedMustAllow, e);
    }

    [Fact]
    public void R03_เงินประกันที่ต้องคืน_ไม่ใช่รายได้_ไม่นับ_ไม่บล็อก()
    {
        var e = DocumentQuotaPolicy.Classify(DocumentType.Receipt, isDeposit: true, fromLodging: true, depositNature: DepositNature.RefundableSecurity);
        Assert.False(DocumentQuotaPolicy.Counts(e));
        Assert.False(DocumentQuotaPolicy.CanRefuseWhenOverQuota(e));
    }

    [Theory]
    [InlineData(DepositNature.PartOfPrice)]
    [InlineData(DepositNature.NonVatSupply)]
    [InlineData(null)]
    public void R03_ทิศตรงข้าม_มัดจำที่เป็นส่วนของราคา_หรือไม่ทราบลักษณะ_ยังนับ(DepositNature? nature)
    {
        var e = DocumentQuotaPolicy.Classify(DocumentType.Receipt, isDeposit: true, fromLodging: false, depositNature: nature);
        Assert.Equal(QuotaEnforcement.CountedMustAllow, e);
    }

    [Fact]
    public void R03_ลักษณะเงินประกัน_บนใบที่ไม่ใช่มัดจำ_ไม่มีผล()
    {
        var e = DocumentQuotaPolicy.Classify(DocumentType.TaxInvoice, isDeposit: false, fromLodging: false, depositNature: DepositNature.RefundableSecurity);
        Assert.Equal(QuotaEnforcement.CountedMustAllow, e);
    }

    [Theory]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Invoice)]
    public void R03_ใบกำกับยอดริบที่อ้างใบมัดจำ_การขายเดิม_ไม่นับ_ไม่บล็อก(DocumentType type)
    {
        var e = DocumentQuotaPolicy.Classify(type, isDeposit: false, fromLodging: true, isDepositForfeitInvoice: true);
        Assert.False(DocumentQuotaPolicy.Counts(e));
        Assert.False(DocumentQuotaPolicy.CanRefuseWhenOverQuota(e));
    }

    [Fact]
    public void R03_ใบแทนใบที่ยกเลิก_ไม่นับซ้ำ_ใบปกติชนิดเดียวกันยังนับ()
    {
        Assert.False(DocumentQuotaPolicy.Counts(DocumentQuotaPolicy.Classify(DocumentType.TaxInvoice, false, true, isReplacement: true)));
        Assert.True(DocumentQuotaPolicy.Counts(DocumentQuotaPolicy.Classify(DocumentType.TaxInvoice, false, true, isReplacement: false)));
    }

    // ── R-04 ขั้น 1: เลื่อนนับไปตอนอนุมัติเฉพาะเส้นอัตโนมัติหน้าเว็บ ──

    [Theory]
    [InlineData("CmsBooking")]
    [InlineData("cmsbooking")]
    [InlineData("site-order")]
    public void R04_ต้นทางอัตโนมัติหน้าเว็บ_เลื่อนนับไปตอนอนุมัติ(string origin)
        => Assert.True(DocumentQuotaPolicy.DefersCountUntilApproval(origin));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Lodging")]
    [InlineData("Pos")]
    [InlineData("Ocr")]
    [InlineData("Import")]
    public void R04_ทิศตรงข้าม_ต้นทางอื่นยังนับตอนสร้างเหมือนเดิม(string? origin)
        => Assert.False(DocumentQuotaPolicy.DefersCountUntilApproval(origin));

    [Fact]
    public void R04_ค่าคงที่ต้นทางตรงกับที่เส้นคำสั่งซื้อใช้()
        => Assert.Equal("site-order", SiteOrderDepositInvoicing.OriginModule);
}
