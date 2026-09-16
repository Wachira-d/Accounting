using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เอกสารที่เก็บยอดไว้ที่ "หัว" โดยไม่มีบรรทัดย่อย (Expense · PaymentVoucher ·
/// CertificateInLieu — ทรงหลักของใบบริการต่างประเทศ) ต้องไม่ได้ยอด 0
///
/// ล็อกสองทิศ: ใบ header-only ต้องได้ยอดจริง **และ** ใบที่มีบรรทัดต้องคิดจาก
/// บรรทัดเหมือนเดิม (ไม่ใช่กลายเป็นยอดหัวเอกสารทุกใบ)
/// </summary>
public class DocumentVatFallbackTests
{
    private static DocumentLine L(decimal amount, decimal vat, bool claimable = true, decimal rate = 7m)
        => new() { Amount = amount, VatAmount = vat, IsVatClaimable = claimable, VatRate = rate };

    // ═══ ทิศที่เคยพัง: ไม่มีบรรทัด ═══

    [Fact]
    public void ไม่มีบรรทัด_ภาษีซื้อต้องมาจากหัวเอกสาร_ไม่ใช่ศูนย์()
    {
        Assert.Equal(70m, DocumentVatFallback.ClaimableVat(new List<DocumentLine>(), 70m));
        Assert.Equal(70m, DocumentVatFallback.ClaimableVat(null, 70m));
    }

    [Fact]
    public void ไม่มีบรรทัด_ฐานภาษีใช้_SubTotal_ก่อน_แล้วค่อยถอยจากยอดรวม()
    {
        Assert.Equal(1_000m, DocumentVatFallback.TaxBase(null, 1_000m, 1_070m, 70m));
        // SubTotal ไม่ถูกกรอก → ถอยจากยอดรวมหัก VAT
        Assert.Equal(1_000m, DocumentVatFallback.TaxBase(null, 0m, 1_070m, 70m));
    }

    // ═══ ทิศที่ต้องไม่เปลี่ยน: มีบรรทัด ═══

    [Fact]
    public void มีบรรทัด_ต้องคิดจากบรรทัดเหมือนเดิม_และเคารพธงเคลมไม่ได้()
    {
        var lines = new List<DocumentLine>
        {
            L(1_000m, 70m),
            L(500m, 35m, claimable: false),   // §82/5 — ห้ามนับเข้าภาษีซื้อ
        };
        // หัวเอกสารมี 105 แต่บรรทัดบอกว่าเคลมได้แค่ 70 → ต้องได้ 70
        Assert.Equal(70m, DocumentVatFallback.ClaimableVat(lines, 105m));
    }

    [Fact]
    public void มีบรรทัด_ฐานภาษีตัดบรรทัดยกเว้น_VAT_ออก()
    {
        var lines = new List<DocumentLine>
        {
            L(1_000m, 70m),
            L(300m, 0m, rate: -1m),           // ยกเว้น §81 — ไม่เข้าฐาน
        };
        Assert.Equal(1_000m, DocumentVatFallback.TaxBase(lines, 9_999m, 9_999m, 9_999m));
    }

    [Fact]
    public void IsHeaderOnly_ตอบตรงกับสิ่งที่อีกสองเมธอดใช้ตัดสิน()
    {
        Assert.True(DocumentVatFallback.IsHeaderOnly(null));
        Assert.True(DocumentVatFallback.IsHeaderOnly(new List<DocumentLine>()));
        Assert.False(DocumentVatFallback.IsHeaderOnly(new List<DocumentLine> { L(1m, 0m) }));
    }
}
