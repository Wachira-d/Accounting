using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>§83/6 reverse charge — การแยกขาเครดิตของใบซื้อบริการต่างประเทศ
///
/// ที่มา (ผู้ใช้รายงาน 2026-09-13): ใบสำคัญจ่าย Booking.com สองใบ — ใบที่อนุมัติแล้ว
/// แสดง JE ถูกต้อง (Cr 21912 ภ.พ.36 + Cr ธนาคารเฉพาะฐาน) แต่ใบใหม่ที่ยังไม่อนุมัติ
/// แสดง "ประมาณการ" ที่ **ไม่มีบรรทัด ภ.พ.36 เลย** และเครดิตเจ้าหนี้ด้วยยอดรวม VAT
/// ⇒ ดูเหมือนระบบเปลี่ยนไปบันทึกผิด ทั้งที่ posting จริงไม่ได้เปลี่ยน —
/// พรีวิวเป็น renderer ตัวที่สามที่ไม่เคยรู้จักกฎนี้ (`git log -S"21912"` บน
/// PdfGenerationService ว่างเปล่า). เทสต์นี้ล็อกกติกาให้ทุก renderer ใช้ร่วมกัน</summary>
public class ForeignServiceVatTests
{
    // เลขจริงจากใบที่ผู้ใช้ส่งมา (PV-20260801-0003 · Booking.com B.V.)
    private const decimal Base = 10_074.75m;
    private const decimal Vat = 705.23m;
    private const decimal Gross = 10_779.98m;

    [Fact]
    public void บริการต่างประเทศ_ผู้รับเงินได้เฉพาะฐาน_VAT_ไปเป็นหนี้_ภพ36()
    {
        var split = ForeignServiceVat.SplitCredit(isForeignService: true, Gross, Vat);

        // ผู้ขาย ตปท. ไม่เก็บ VAT ไทย ⇒ จ่าย/ตั้งหนี้เขาแค่ฐาน
        Assert.Equal(Base, split.PayeeCredit);
        // VAT ที่ประเมินเองเป็นหนี้ต่อสรรพากร ไม่ใช่ต่อผู้ขาย
        Assert.Equal(Vat, split.Pp36Credit);
        // ขาเครดิตรวมต้องเท่าเดิมเสมอ — JE ต้องยังบาลานซ์
        Assert.Equal(Gross, split.PayeeCredit + split.Pp36Credit);
    }

    [Fact]
    public void ใบในประเทศ_ต้องไม่ถูกแตะ_ขาเครดิตเป็นยอดรวมเหมือนเดิม()
    {
        // ทิศตรงข้าม — ถ้าเทสต์มีแต่เคสต่างประเทศ การ "แยกทุกใบ" ก็ผ่านได้
        var split = ForeignServiceVat.SplitCredit(isForeignService: false, Gross, Vat);

        Assert.Equal(Gross, split.PayeeCredit);
        Assert.Equal(0m, split.Pp36Credit);
    }

    [Theory]
    // ใบต่างประเทศที่ไม่มี VAT (ผู้ขายที่ไม่อยู่ในบังคับ / บริการใช้นอกไทย)
    // ⇒ ไม่มีหนี้ ภ.พ.36 ให้ตั้ง — ห้ามสร้างบรรทัด 21912 ยอด 0
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    // ค่าติดลบ (ใบลดหนี้) ต้องไม่ถูกตีเป็นหนี้ ภ.พ.36 — เส้นนั้นมีกติกาของตัวเอง
    [InlineData(true, -100)]
    public void ไม่มี_VAT_หรือ_VAT_ติดลบ_ต้องไม่ตั้งหนี้_ภพ36(bool foreign, int vatWhole)
    {
        // รับเป็น int แล้วแปลงเอง — InlineData กับพารามิเตอร์ decimal ต้องพึ่ง
        // การแปลงชนิดของ xUnit ซึ่งไม่คุ้มจะเสี่ยงในเทสต์ที่คอมไพล์ที่นี่ไม่ได้
        decimal vat = vatWhole;

        Assert.Equal(0m, ForeignServiceVat.SelfAssessedVat(foreign, vat));

        var split = ForeignServiceVat.SplitCredit(foreign, Gross, vat);
        Assert.Equal(Gross, split.PayeeCredit);
        Assert.Equal(0m, split.Pp36Credit);
    }

    [Fact]
    public void ยอดเครดิตที่ส่งเข้ามาเป็นของผู้เรียก_WHT_คนละเรื่องกับ_มาตรา83_6()
    {
        // สาย accrual (cash basis) ตัดเจ้าหนี้ gross = ยอดจ่าย + WHT ก่อนแยก ภ.พ.36
        // — กติกา WHT อยู่นอกฟังก์ชันนี้ ตัวมันรู้แค่ "ก้อนที่ส่งมา ลบ VAT ออก"
        const decimal wht = 302.24m;
        var split = ForeignServiceVat.SplitCredit(true, Gross + wht, Vat);

        Assert.Equal(Base + wht, split.PayeeCredit);
        Assert.Equal(Vat, split.Pp36Credit);
    }

    [Fact]
    public void ผังบัญชีที่กติกาอ้าง_ต้องเป็นชุดเดียวกับที่_JE_จริงใช้()
    {
        // คอนสแตนต์สองตัวนี้คือสิ่งที่ทำให้ posting จริงกับพรีวิวพูดตรงกัน —
        // เปลี่ยนเมื่อไรต้องเปลี่ยนพร้อมกันทั้งสองฝั่ง
        Assert.Equal("21912", ForeignServiceVat.Pp36PayableCode);
        Assert.Equal("11640", ForeignServiceVat.Pp36InputVatCode);
    }

    // ═════════════════ A5b (บอร์ดตัดสิน 2026-10-13 · ทีม B): ฐาน ภ.พ.36 ครอบ 50 ทวิ "ออกให้ครั้งเดียว" ═════════════════
    // เดิมรายงาน ภ.พ.36 + คำเตือนตอนออก 50 ทวิ นับแค่ "ออกให้ตลอดไป" ⇒ ส่วนที่ผู้จ่ายออกแทนรอบแรกของใบ "ครั้งเดียว" (เงินได้ของผู้รับ ม.39 ·
    // ส่วนของมูลค่าบริการ §79) หายจากฐานเงียบ ๆ

    [Fact]
    public void A5b_ครั้งเดียว_บรรทัดยังไม่รวมส่วนที่ออกแทน_บวกส่วนต่างเงินได้ลบยอดบรรทัด()
    {
        // ตัวเลขจากตัวตั้งเดียว WhtGrossUp.OnBase (ไม่พิมพ์สูตรซ้ำ): 450 @15% ครั้งเดียว ⇒ ออกแทน 67.50 · เงินได้ 517.50 · ภาษี 77.63
        var once = WhtGrossUp.OnBase(450m, 15m, WithholdingTaxCertType.PayOnce);
        Assert.Equal(67.50m, once.PayerBorne);

        var borne = ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayOnce, 450m, 0m, once.CertIncome, once.Tax);
        Assert.Equal(once.PayerBorne, borne);
        Assert.Equal(517.50m, ForeignServiceVat.Pp36Base(450m, borne));
        Assert.Equal(36.23m, ForeignServiceVat.SelfAssessedVatOn(ForeignServiceVat.Pp36Base(450m, borne)));
        // คำเตือน "VAT ขาด": ภ.พ.36 ที่คิดจากบรรทัด 31.50 ขาด 4.73
        Assert.Equal(4.73m, ForeignServiceVat.Pp36Shortfall(450m, borne, 31.50m));
        // ตัวอย่างในคู่มือ 10,000 @3% ⇒ 300
        var big = WhtGrossUp.OnBase(10_000m, 3m, WithholdingTaxCertType.PayOnce);
        Assert.Equal(300m, ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayOnce, 10_000m, 0m, big.CertIncome, big.Tax));
    }

    [Fact]
    public void A5b_ทิศตรงข้าม_ครั้งเดียว_บรรทัดคีย์รวมส่วนที่ออกแทนแล้ว_ไม่บวกซ้ำ_ไม่เตือน()
    {
        // บรรทัด 517.50 = เงินได้บน 50 ทวิ ⇒ รวมแล้ว (มีหรือไม่มี WHT บนเอกสารก็ตาม)
        Assert.Equal(0m, ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayOnce, 517.50m, 77.63m, 517.50m, 77.63m));
        Assert.Equal(0m, ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayOnce, 517.50m, 0m, 517.50m, 77.63m));
        Assert.Equal(0m, ForeignServiceVat.Pp36Shortfall(517.50m, 0m, 36.23m));
        // ไม่มีภาษีบนใบ ⇒ 0
        Assert.Equal(0m, ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayOnce, 450m, 0m, 450m, 0m));
    }

    [Fact]
    public void A5b_ครั้งเดียว_เอกสารตั้งWHTภาษีเต็มแต่บรรทัดคือฐาน_ยังบวกส่วนที่ออกแทน_เหมือนตลอดไป()
    {
        // ใบ "ครั้งเดียว" ตั้ง WithholdingTaxAmount = ภาษีนำส่งทั้งก้อน (309) และบรรทัด = ฐาน 10,000 ⇒ เงินได้ 10,300 = บรรทัด + 300 พอดี
        // ⇒ ส่วนที่ออกแทนอยู่นอกบรรทัดจริง (กิ่งเดียวกับ "ตลอดไป" ที่ตรวจเงินได้ ≈ บรรทัด + ภาษี ก่อนดู WHT บนเอกสาร)
        Assert.Equal(300m, ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayOnce, 10_000m, 309m, 10_300m, 309m));
        Assert.Equal(309.28m, ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayAlways, 10_000m, 309.28m, 10_309.28m, 309.28m));
    }

    [Fact]
    public void A5b_ครั้งเดียว_50ทวิครอบบางงวด_ตัดสินจากWHTบนเอกสาร_ไม่ใช้ส่วนต่างติดลบ()
    {
        // บรรทัด 900 (สองงวด) · 50 ทวิ งวดแรก 517.50/77.63 · เอกสารไม่มี WHT (ภาษีจ่ายแยก) ⇒ บวกส่วนที่ออกแทนบนใบ 67.50 (ไม่ใช่ 517.50 − 900)
        Assert.Equal(67.50m, ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayOnce, 900m, 0m, 517.50m, 77.63m));
        // เอกสารมี WHT บนตัว ⇒ บรรทัดคือเงินได้รวม ⇒ 0
        Assert.Equal(0m, ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayOnce, 1_035m, 155.26m, 517.50m, 77.63m));
        // 50 ทวิ เงินได้สูงกว่าบรรทัดแต่ไม่ตรงสูตรครั้งเดียว (ใบครอบเกินเอกสาร) ⇒ ไม่ใช้ส่วนต่างที่เกินภาษีบนใบ — ใช้ส่วนที่ออกแทนบนใบ
        Assert.Equal(67.50m, ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayOnce, 100m, 0m, 517.50m, 77.63m));
    }

    [Fact]
    public void A5b_หักณที่จ่าย_ไม่มีส่วนที่ออกแทน_และตลอดไปยังได้ตัวเลขเดิม()
    {
        // หัก ณ ที่จ่าย: ผู้รับรับภาระเอง ⇒ ฐาน ภ.พ.36 ไม่ขยับเลย ไม่ว่าเงินได้บนใบเป็นเท่าไร
        Assert.Equal(0m, ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.Withhold, 450m, 0m, 529.41m, 79.41m));
        // ตลอดไป — ตัวเลขเดิมของ R2M-3 ไม่ถูกแตะ (529.41 ⇒ 37.06 · gross-up แล้ว 0)
        Assert.Equal(79.41m, ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayAlways, 450m, 0m, 529.41m, 79.41m));
        Assert.Equal(0m, ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayAlways, 529.41m, 79.41m, 529.41m, 79.41m));
    }
}
