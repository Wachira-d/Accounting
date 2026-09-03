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
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.Quotation)]
    public void เอกสารจากโมดูลที่พัก_ไม่นับโควตาเอกสาร_เพราะมีมิเตอร์การเข้าพักแล้ว(DocumentType type)
    {
        var e = DocumentQuotaPolicy.Classify(type, isDeposit: false, fromLodging: true);
        Assert.False(DocumentQuotaPolicy.Counts(e));
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
}
